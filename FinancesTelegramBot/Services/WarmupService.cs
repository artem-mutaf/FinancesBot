using FinancesTelegramBot.Api;
using FinancesTelegramBot.Data;
using Microsoft.EntityFrameworkCore;

namespace FinancesTelegramBot.Services;

/// <summary>
/// "Прогрев" при запуске приложения.
///
/// Самый первый запрос к API медленный (~1 с), хотя следующие — 15-30 мс. Причины:
///  - EF Core строит модель (читает конфигурацию из OnModelCreating);
///  - каждый LINQ-запрос при первом выполнении переводится в SQL, результат кэшируется;
///  - открывается первое соединение с PostgreSQL (дальше берётся из пула соединений);
///  - JIT-компилятор переводит в машинный код методы, которые ещё ни разу не вызывались.
///
/// Выполняем те же запросы заранее, с несуществующим пользователем (Id = 0).
/// Кэш EF запоминает "форму" запроса, а не значения параметров, — поэтому реальный
/// пользователь получит уже готовый SQL.
///
/// BackgroundService: приложение не ждёт окончания прогрева и сразу принимает запросы.
/// </summary>
public sealed class WarmupService(IServiceScopeFactory scopeFactory, ILogger<WarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            const long nobody = 0;

            await db.Users.FindAsync([nobody], stoppingToken);
            await db.GetAccountDtosAsync(nobody, stoppingToken);
            await db.GetCategoryDtosAsync(nobody, stoppingToken);
            await db.Operations
                .Where(o => o.UserId == nobody)
                .OrderNewestFirst()
                .Take(50)
                .SelectOperationDtos()
                .ToListAsync(stoppingToken);

            logger.LogInformation("Прогрев БД завершён за {Elapsed} мс", sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Прогрев — оптимизация, а не обязательный шаг: если не вышло, приложение работает дальше.
            logger.LogWarning(ex, "Прогрев БД не удался");
        }
    }
}
