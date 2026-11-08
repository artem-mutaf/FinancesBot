using System.Text.Json.Serialization;
using FinancesTelegramBot.Api;
using FinancesTelegramBot.Auth;
using FinancesTelegramBot.Bot;
using FinancesTelegramBot.Data;
using FinancesTelegramBot.Services;
using Microsoft.AspNetCore.Authentication;
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

// enum в JSON — строкой ("Card"), а не числом (0). Фронтенду так понятнее.
// allowIntegerValues: false — иначе клиент мог бы прислать {"type": 42}, и в enum
// попало бы несуществующее значение. Теперь принимаются только имена из enum, остальное — 400.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));

// ---------- Telegram-бот ----------
// Singleton: один клиент на всё приложение (внутри у него HttpClient, его не нужно создавать заново).
builder.Services.AddSingleton<ITelegramBotClient>(sp =>
    new TelegramBotClient(sp.GetRequiredService<IOptions<BotOptions>>().Value.Token));
builder.Services.AddSingleton<UpdateHandler>();
builder.Services.AddHostedService<BotPollingService>();

var app = builder.Build();

// ---------- HTTP pipeline ----------
// Порядок middleware важен: запрос проходит их сверху вниз.
app.UseDefaultFiles();   // "/" -> "/index.html"
app.UseStaticFiles();    // отдаём файлы из wwwroot (это и есть Mini App)
app.UseAuthentication(); // кто ты? (заполняет HttpContext.User)
app.UseAuthorization();  // можно ли тебе сюда? (проверяет RequireAuthorization)

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));
app.MapMeEndpoints();
app.MapAccountsEndpoints();
app.MapCategoriesEndpoints();
app.MapOperationsEndpoints();

app.Run();
