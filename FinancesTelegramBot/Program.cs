using System.Text.Json.Serialization;
using FinancesTelegramBot.Api;
using FinancesTelegramBot.Auth;
using FinancesTelegramBot.Bot;
using FinancesTelegramBot.Data;
using FinancesTelegramBot.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

// ---------- Конфигурация ----------
// Привязываем секцию "Bot" к классу BotOptions и проверяем при старте, что токен задан.
builder.Services.AddOptions<BotOptions>()
    .Bind(builder.Configuration.GetSection(BotOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Token),
        "Не задан Bot:Token. Выполни: dotnet user-secrets set \"Bot:Token\" \"<токен>\"")
    // Telegram открывает Mini App только по HTTPS — ловим опечатку в настройках сразу при запуске
    .Validate(o => string.IsNullOrEmpty(o.WebAppUrl) || o.WebAppUrl.StartsWith("https://"),
        "Bot:WebAppUrl должен начинаться с https://")
    .ValidateOnStart();

// ---------- База данных ----------
// Строка подключения берётся из ConnectionStrings:Default (храним в user-secrets — там пароль).
// AddDbContext регистрирует AppDbContext как Scoped — новый экземпляр на каждый HTTP-запрос.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// ---------- Аутентификация через Telegram initData ----------
builder.Services.AddSingleton<TelegramInitDataValidator>();
builder.Services
    .AddAuthentication(TelegramAuthHandler.SchemeName) // схема по умолчанию
    .AddScheme<AuthenticationSchemeOptions, TelegramAuthHandler>(TelegramAuthHandler.SchemeName, null);
builder.Services.AddAuthorization();

// Все ошибки (400, 404, 500, кривой JSON) — в едином JSON-формате ProblemDetails,
// а не HTML-страницей или текстом. Фронтенду проще разбирать.
builder.Services.AddProblemDetails();

// ---------- Сервисы приложения ----------
// Scoped, потому что зависит от AppDbContext (тоже Scoped).
builder.Services.AddScoped<UserProvisioningService>();
builder.Services.AddScoped<OperationService>(); // общая логика операций для API и бота

// Кэш в памяти процесса: там бот держит операции, ждущие выбора категории.
builder.Services.AddMemoryCache();

// ---------- Производительность ----------
// Прогрев EF Core при запуске — чтобы первый пользователь не ждал лишнюю секунду.
builder.Services.AddHostedService<WarmupService>();

// Сжатие ответов: app.js 60 КБ -> ~15 КБ, JSON — в 4-8 раз меньше.
// Через медленную сеть (туннель, мобильный интернет) это заметно ускоряет загрузку.
builder.Services.AddResponseCompression(options =>
{
    // Сжимать и по HTTPS. Обычно по HTTPS это выключено из-за атаки BREACH, но она
    // опасна, когда браузер сам подставляет секрет (cookie) в запросы с чужого сайта.
    // У нас авторизация — заголовок Authorization, который подставляет только наш JS.
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>(); // Brotli жмёт лучше gzip
    options.Providers.Add<GzipCompressionProvider>();   // запасной — для старых клиентов
});
// Уровень сжатия: по умолчанию Fastest (быстро, но слабо). Optimal жмёт заметно сильнее,
// а на наших объёмах (десятки КБ) стоит лишь доли миллисекунды процессора.
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Optimal);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Optimal);

// enum в JSON — строкой ("Card"), а не числом (0). Фронтенду так понятнее.
// allowIntegerValues: false — иначе клиент мог бы прислать {"type": 42}, и в enum
// попало бы несуществующее значение. Теперь принимаются только имена из enum, остальное — 400.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));

// ---------- Telegram-бот ----------
// Singleton: один клиент на всё приложение (внутри у него HttpClient, его не нужно создавать заново).
builder.Services.AddSingleton<ITelegramBotClient>(sp =>
    new TelegramBotClient(sp.GetRequiredService<IOptions<BotOptions>>().Value.Token));
builder.Services.AddSingleton<WebAppUrlProvider>();
builder.Services.AddSingleton<UpdateHandler>();
builder.Services.AddScoped<ChatHandler>(); // создаётся на каждый апдейт внутри UpdateHandler
builder.Services.AddHostedService<BotPollingService>();

// ---------- Dev-туннель (только при разработке) ----------
// Сам поднимает HTTPS-туннель и подставляет его адрес в бота. Включается в appsettings.Development.json.
builder.Services.Configure<DevTunnelOptions>(builder.Configuration.GetSection(DevTunnelOptions.SectionName));
if (builder.Environment.IsDevelopment() &&
    builder.Configuration.GetValue<bool>($"{DevTunnelOptions.SectionName}:Enabled"))
{
    builder.Services.AddHostedService<DevTunnelService>();
}

var app = builder.Build();

// ---------- HTTP pipeline ----------
// Порядок middleware важен: запрос проходит их сверху вниз.
app.UseResponseCompression(); // до остальных — чтобы сжимать их ответы
app.UseDefaultFiles();   // "/" -> "/index.html"
app.UseStaticFiles(new StaticFileOptions // отдаём файлы из wwwroot (это и есть Mini App)
{
    // "no-cache" — это НЕ "не кэшировать". Это "кэшируй, но перед использованием спроси сервер,
    // не изменился ли файл". Сервер сверит ETag (отпечаток файла) и ответит 304 Not Modified
    // без тела — файл не скачивается заново. А после обновления app.js телефон сразу получит новый.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.UseAuthentication(); // кто ты? (заполняет HttpContext.User)
app.UseAuthorization();  // можно ли тебе сюда? (проверяет RequireAuthorization)

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));
app.MapBootstrapEndpoints();
app.MapMeEndpoints();
app.MapAccountsEndpoints();
app.MapCategoriesEndpoints();
app.MapOperationsEndpoints();
app.MapStatsEndpoints();

// ---------- Миграции БД при запуске ----------
// На сервере некому запускать "dotnet ef database update" руками, поэтому приложение
// само применяет недостающие миграции перед стартом. Уже применённые пропускаются
// (EF помнит их в таблице __EFMigrationsHistory), так что повторный запуск безопасен.
// Scope нужен, потому что AppDbContext — Scoped, а мы сейчас вне HTTP-запроса.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();
