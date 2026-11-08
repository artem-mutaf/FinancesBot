using System.Security.Claims;
using FinancesTelegramBot.Auth;
using FinancesTelegramBot.Data;
using FinancesTelegramBot.Data.Entities;
using FinancesTelegramBot.Services;
using Microsoft.EntityFrameworkCore;

namespace FinancesTelegramBot.Api;

/// <summary>Всё, что нужно главному экрану, — одним ответом.</summary>
public sealed record BootstrapDto(
    MeDto Me,
    List<AccountDto> Accounts,
    List<CategoryDto> Categories,
    List<OperationDto> Operations,
    MonthTotalsDto Month);

public static class BootstrapEndpoints
{
    private const int RecentOperations = 50;

    public static IEndpointRouteBuilder MapBootstrapEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/bootstrap", GetBootstrap).RequireAuthorization();
        return app;
    }

    /// <summary>
    /// GET /api/bootstrap?timeZone=Europe/Chisinau — стартовые данные Mini App.
    ///
    /// Зачем: раньше при открытии шло 5 запросов, причём в два "круга" (сначала /api/me,
    /// потом ещё 4). Каждый круг через интернет/туннель — это задержка сети (latency),
    /// которая намного больше, чем время самих SQL-запросов (единицы миллисекунд).
    /// Один запрос = один круг.
    ///
    /// Запросы к БД внутри идут последовательно: один DbContext не умеет выполнять
    /// несколько запросов параллельно. Но они быстрые, и все — в рамках одного соединения.
    /// </summary>
    private static async Task<BootstrapDto> GetBootstrap(
        ClaimsPrincipal principal, UserProvisioningService provisioning, AppDbContext db, CancellationToken ct,
        string? timeZone = null)
    {
        var user = await provisioning.EnsureUserAsync(
            principal.GetUserId(), principal.GetFirstName(), principal.GetUsername(), ct, timeZone);

        var accounts = await db.GetAccountDtosAsync(user.Id, ct);
        var categories = await db.GetCategoryDtosAsync(user.Id, ct);
        var operations = await db.Operations
            .Where(o => o.UserId == user.Id)
            .OrderNewestFirst()
            .Take(RecentOperations)
            .SelectOperationDtos()
            .ToListAsync(ct);

        // Итоги текущего месяца (для карточки баланса) — по календарю пользователя.
        // Раньше для этого дёргали всю статистику (3 тяжёлых запроса), хватает одного.
        var today = UserClock.Today(user.TimeZone);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var sums = await db.Operations
            .Where(o => o.UserId == user.Id && o.Date >= monthStart && o.Date <= monthEnd
                        && o.Type != OperationType.Transfer)
            .GroupBy(o => o.Type) // максимум две строки: Income и Expense
            .Select(g => new { Type = g.Key, Amount = g.Sum(o => o.Amount) })
            .ToDictionaryAsync(x => x.Type, x => x.Amount, ct);

        var month = new MonthTotalsDto(today.Year, today.Month,
            Income: sums.GetValueOrDefault(OperationType.Income),
            Expense: sums.GetValueOrDefault(OperationType.Expense));

        return new BootstrapDto(MeEndpoints.ToDto(user), accounts, categories, operations, month);
    }
}
