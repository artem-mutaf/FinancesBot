using System.Security.Claims;
using FinancesTelegramBot.Auth;
using FinancesTelegramBot.Data;
using FinancesTelegramBot.Data.Entities;
using FinancesTelegramBot.Services;
using Microsoft.EntityFrameworkCore;

namespace FinancesTelegramBot.Api;

/// <summary>
/// HTTP-слой для операций: разбирает запрос, вызывает OperationService и превращает
/// результат в HTTP-ответ. Сама бизнес-логика (валидация, запись) — в OperationService,
/// потому что её же использует бот.
/// </summary>
public static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/operations").RequireAuthorization();

        group.MapGet("/", GetOperations);
        group.MapGet("/daily", GetDailySummary);
        group.MapPost("/", CreateOperation);
        group.MapPut("/{id:int}", UpdateOperation);
        group.MapDelete("/{id:int}", DeleteOperation);

        return app;
    }

    /// <summary>
    /// GET /api/operations?limit=50                       — последние операции, свежие сверху;
    /// GET /api/operations?from=2026-11-08&amp;to=2026-11-08  — операции за период (например, за день).
    ///
    /// Все параметры — из query string. "int limit = 50" — значение по умолчанию;
    /// "DateOnly? from = null" — необязательный фильтр (ASP.NET сам распарсит "2026-11-08").
    /// </summary>
    private static async Task<List<OperationDto>> GetOperations(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken ct,
        int limit = 50, DateOnly? from = null, DateOnly? to = null)
    {
        var userId = principal.GetUserId();
        limit = Math.Clamp(limit, 1, 500); // защита от ?limit=1000000

        // IQueryable можно достраивать по условию: Where добавится в SQL, только если фильтр передан.
        var query = db.Operations.Where(o => o.UserId == userId);
        if (from is { } fromDate)
            query = query.Where(o => o.Date >= fromDate);
        if (to is { } toDate)
            query = query.Where(o => o.Date <= toDate);

        return await query
            .OrderByDescending(o => o.Date)
            .ThenByDescending(o => o.CreatedAt) // внутри одного дня — по времени добавления
            .ThenByDescending(o => o.Id)
            .Take(limit)                        // SQL LIMIT
            .SelectOperationDtos()
            .ToListAsync(ct);
    }

    /// <summary>
    /// GET /api/operations/daily?from=2026-11-01&amp;to=2026-11-30 — итоги по каждому дню периода.
    /// Возвращаются только дни, в которых есть операции.
    /// </summary>
    private static async Task<IResult> GetDailySummary(
        DateOnly from, DateOnly to, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (from > to)
            errors.Add("from", "Начало периода позже конца");
        else if (to.DayNumber - from.DayNumber > 366) // DayNumber — номер дня от 01.01.0001, удобно для разницы
            errors.Add("to", "Период не больше года");
        if (errors.HasErrors)
            return errors.ToResult();

        var userId = principal.GetUserId();

        // Внимание: не любой LINQ EF умеет перевести в SQL. Первая версия запроса заканчивалась на
        //   .Select(g => new DaySummaryDto(...)).OrderBy(d => d.Date)
        // и падала с "could not be translated": EF не знает, что свойство d.Date record'а —
        // это первый аргумент конструктора (g.Key), и не может отсортировать по нему в SQL.
        // Правило: сортировать/фильтровать ДО проекции в DTO, по полям сущности или ключу группы.
        var days = await db.Operations
            .Where(o => o.UserId == userId && o.Date >= from && o.Date <= to)
            .GroupBy(o => o.Date)
            .OrderBy(g => g.Key) // g.Key — то, по чему группировали (дата)
            .Select(g => new DaySummaryDto(
                g.Key,
                g.Where(o => o.Type == OperationType.Income).Sum(o => o.Amount),
                g.Where(o => o.Type == OperationType.Expense).Sum(o => o.Amount),
                g.Count()))
            .ToListAsync(ct);

        return Results.Ok(days);
    }

    /// <summary>POST /api/operations — добавить доход, расход или перевод.</summary>
    private static async Task<IResult> CreateOperation(
        OperationRequest request, ClaimsPrincipal principal, OperationService operations, CancellationToken ct)
    {
        var result = await operations.CreateAsync(principal.GetUserId(), request, ct);

        // switch-выражение по типу результата: каждый вариант -> свой HTTP-ответ
        return result switch
        {
            OperationResult.Success s => Results.Created($"/api/operations/{s.Operation.Id}", s.Operation),
            OperationResult.Invalid i => i.Errors.ToResult(),
            _ => Results.NotFound(),
        };
    }

    /// <summary>PUT /api/operations/{id} — изменить операцию целиком (можно сменить даже тип).</summary>
    private static async Task<IResult> UpdateOperation(
        int id, OperationRequest request, ClaimsPrincipal principal, OperationService operations,
        CancellationToken ct)
    {
        var result = await operations.UpdateAsync(principal.GetUserId(), id, request, ct);

        return result switch
        {
            OperationResult.Success s => Results.Ok(s.Operation),
            OperationResult.Invalid i => i.Errors.ToResult(),
            _ => Results.NotFound(),
        };
    }

    /// <summary>DELETE /api/operations/{id} — баланс счёта пересчитается сам, ведь он вычисляемый.</summary>
    private static async Task<IResult> DeleteOperation(
        int id, ClaimsPrincipal principal, OperationService operations, CancellationToken ct)
    {
        var deleted = await operations.DeleteAsync(principal.GetUserId(), id, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }
}
