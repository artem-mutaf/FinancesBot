using System.Security.Claims;
using FinancesTelegramBot.Auth;
using FinancesTelegramBot.Data;
using FinancesTelegramBot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinancesTelegramBot.Api;

// ---------- DTO статистики ----------

/// <summary>Сумма по одной категории за месяц.</summary>
public sealed record CategoryStatDto(int CategoryId, string Name, string? Icon, decimal Amount, int Count);

/// <summary>Итоги одного месяца (для графика динамики).</summary>
public sealed record MonthTotalsDto(int Year, int Month, decimal Income, decimal Expense);

/// <summary>Всё для экрана статистики одним ответом.</summary>
public sealed record MonthStatsDto(
    int Year,
    int Month,
    MonthTotalsDto Current,
    MonthTotalsDto Previous,
    List<CategoryStatDto> ExpenseByCategory,
    List<CategoryStatDto> IncomeByCategory,
    List<MonthTotalsDto> Trend); // последние N месяцев, включая текущий, от старого к новому

public static class StatsEndpoints
{
    private const int TrendMonths = 6;

    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/stats", GetMonthStats).RequireAuthorization();
        return app;
    }

    /// <summary>
    /// GET /api/stats?year=2026&amp;month=11 — статистика за месяц.
    /// Переводы в статистику не попадают: это не доход и не расход, деньги остаются у тебя.
    /// </summary>
    private static async Task<IResult> GetMonthStats(
        int year, int month, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (month is < 1 or > 12) // pattern matching: "month < 1 или month > 12"
            errors.Add("month", "Месяц должен быть от 1 до 12");
        if (year is < 2000 or > 2100)
            errors.Add("year", "Неверный год");
        if (errors.HasErrors)
            return errors.ToResult();

        var userId = principal.GetUserId();

        var monthStart = new DateOnly(year, month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);  // последний день месяца
        var trendStart = monthStart.AddMonths(-(TrendMonths - 1)); // 1-е число месяца 5 месяцев назад

        // Базовый запрос: операции пользователя, без переводов. IQueryable — ещё не выполнен.
        var incomeAndExpense = db.Operations
            .Where(o => o.UserId == userId && o.Type != OperationType.Transfer);

        // ---------- Запрос 1: суммы по категориям за месяц ----------
        // Группируем сразу по нескольким полям — анонимный тип new { ... } как составной ключ.
        // Обращение к o.Category.Name EF превратит в JOIN с таблицей Categories.
        var byCategory = await incomeAndExpense
            .Where(o => o.Date >= monthStart && o.Date <= monthEnd)
            .GroupBy(o => new { o.CategoryId, o.Category!.Name, o.Category.Icon, o.Type })
            .Select(g => new
            {
                g.Key.CategoryId,
                g.Key.Name,
                g.Key.Icon,
                g.Key.Type,
                Amount = g.Sum(o => o.Amount),
                Count = g.Count(),
            })
            .OrderByDescending(x => x.Amount) // по анонимному типу сортировать можно (в отличие от record-конструктора)
            .ToListAsync(ct);

        // Дальше — обычный LINQ to Objects в памяти: данных уже немного (по строке на категорию).
        List<CategoryStatDto> ForType(OperationType type) => byCategory
            .Where(x => x.Type == type)
            .Select(x => new CategoryStatDto(x.CategoryId!.Value, x.Name, x.Icon, x.Amount, x.Count))
            .ToList();

        // ---------- Запрос 2: итоги по месяцам для графика динамики ----------
        // o.Date.Year / o.Date.Month Npgsql переводит в date_part('year', ...) и т.п.
        var monthly = await incomeAndExpense
            .Where(o => o.Date >= trendStart && o.Date <= monthEnd)
            .GroupBy(o => new { o.Date.Year, o.Date.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                Income = g.Where(o => o.Type == OperationType.Income).Sum(o => o.Amount),
                Expense = g.Where(o => o.Type == OperationType.Expense).Sum(o => o.Amount),
            })
            .ToListAsync(ct);

        // В месяцах без операций GROUP BY ничего не вернёт — достраиваем нули сами,
        // чтобы на графике были все 6 столбцов. Словарь — для быстрого поиска по (год, месяц).
        var monthlyLookup = monthly.ToDictionary(m => (m.Year, m.Month)); // ключ — кортеж
        var trend = Enumerable.Range(0, TrendMonths)
            .Select(i => trendStart.AddMonths(i))
            .Select(d => monthlyLookup.TryGetValue((d.Year, d.Month), out var m)
                ? new MonthTotalsDto(d.Year, d.Month, m.Income, m.Expense)
                : new MonthTotalsDto(d.Year, d.Month, 0, 0))
            .ToList();

        // Текущий и прошлый месяц — последние два элемента trend (^1 — "первый с конца", C# 8).
        return Results.Ok(new MonthStatsDto(
            year,
            month,
            Current: trend[^1],
            Previous: trend[^2],
            ExpenseByCategory: ForType(OperationType.Expense),
            IncomeByCategory: ForType(OperationType.Income),
            Trend: trend));
    }
}
