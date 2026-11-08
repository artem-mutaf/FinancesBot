using System.Security.Claims;
using FinancesTelegramBot.Auth;
using FinancesTelegramBot.Data;
using FinancesTelegramBot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinancesTelegramBot.Api;

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
    ///
    /// GroupBy + агрегаты EF переводит в один SQL:
    ///   SELECT "Date", SUM(CASE WHEN "Type"='Income' THEN "Amount" ELSE 0 END), ..., COUNT(*)
    ///   FROM "Operations" WHERE ... GROUP BY "Date"
    /// Считает PostgreSQL — в память приходит максимум 31 строка, а не все операции месяца.
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
        OperationRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();

        var errors = new ValidationErrors();
        var valid = await ValidateAsync(request, userId, db, errors, ct);
        if (valid is null)
            return errors.ToResult();

        var operation = new Operation { UserId = userId, CreatedAt = DateTime.UtcNow };
        Apply(valid, operation);
        db.Operations.Add(operation);
        await db.SaveChangesAsync(ct);

        // Перечитываем через ту же проекцию, чтобы вернуть названия счёта и категории.
        var dto = await db.Operations.Where(o => o.Id == operation.Id).SelectOperationDtos().SingleAsync(ct);
        return Results.Created($"/api/operations/{operation.Id}", dto);
    }

    /// <summary>
    /// PUT /api/operations/{id} — изменить операцию целиком (можно даже сменить тип:
    /// расход -> перевод). Баланс пересчитается сам: он вычисляется из операций.
    /// </summary>
    private static async Task<IResult> UpdateOperation(
        int id, OperationRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();

        var operation = await db.Operations.SingleOrDefaultAsync(o => o.Id == id && o.UserId == userId, ct);
        if (operation is null)
            return Results.NotFound();

        var errors = new ValidationErrors();
        var valid = await ValidateAsync(request, userId, db, errors, ct);
        if (valid is null)
            return errors.ToResult();

        // CreatedAt НЕ трогаем: это время создания записи, а не изменения.
        Apply(valid, operation);
        await db.SaveChangesAsync(ct); // EF сам сгенерирует UPDATE только по изменённым полям

        var dto = await db.Operations.Where(o => o.Id == id).SelectOperationDtos().SingleAsync(ct);
        return Results.Ok(dto);
    }

    /// <summary>DELETE /api/operations/{id} — баланс счёта пересчитается сам, ведь он вычисляемый.</summary>
    private static async Task<IResult> DeleteOperation(
        int id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();

        // ExecuteDeleteAsync — сразу "DELETE FROM ... WHERE ...", без загрузки объекта в память.
        // Возвращает число удалённых строк: 0 => операции нет или она чужая.
        var deleted = await db.Operations
            .Where(o => o.Id == id && o.UserId == userId)
            .ExecuteDeleteAsync(ct);

        return deleted == 0 ? Results.NotFound() : Results.NoContent();
    }

    // =====================================================================
    //  Валидация — общая для создания и редактирования
    // =====================================================================

    /// <summary>Проверенные и нормализованные данные операции — готовы к записи в сущность.</summary>
    private sealed record ValidOperation(
        OperationType Type,
        decimal Amount,
        int AccountId,
        int? ToAccountId,
        int? CategoryId,
        string? Description,
        DateOnly Date);

    /// <summary>Переносит проверенные данные в сущность (и для новой, и для существующей).</summary>
    private static void Apply(ValidOperation valid, Operation operation)
    {
        operation.Type = valid.Type;
        operation.Amount = valid.Amount;
        operation.AccountId = valid.AccountId;
        operation.ToAccountId = valid.ToAccountId;
        operation.CategoryId = valid.CategoryId;
        operation.Description = valid.Description;
        operation.Date = valid.Date;
    }

    /// <summary>
    /// Проверяет запрос. Возвращает ValidOperation, если всё хорошо, иначе null
    /// (а ошибки складывает в errors).
    /// </summary>
    private static async Task<ValidOperation?> ValidateAsync(
        OperationRequest request, long userId, AppDbContext db, ValidationErrors errors, CancellationToken ct)
    {
        // ---------- Сумма ----------
        if (request.Amount <= 0)
            errors.Add("amount", "Сумма должна быть больше нуля");
        else if (request.Amount >= ValidationErrors.MaxAmount)
            errors.Add("amount", "Слишком большая сумма");
        else if (!ValidationErrors.HasAtMostTwoDecimals(request.Amount))
            errors.Add("amount", "Не больше двух знаков после запятой");

        // ---------- Счёт (источник) ----------
        // Каждый id от клиента проверяем на принадлежность пользователю!
        // Иначе можно было бы прислать чужой AccountId и списать деньги с чужого счёта.
        var accountExists = await db.Accounts.AnyAsync(a => a.Id == request.AccountId && a.UserId == userId, ct);
        if (!accountExists)
            errors.Add("accountId", "Счёт не найден");

        // ---------- Зависит от типа операции ----------
        int? toAccountId = null;
        int? categoryId = null;

        switch (request.Type)
        {
            case OperationType.Transfer:
                if (request.ToAccountId is not { } targetId) // pattern: "не null, и назови значение targetId"
                    errors.Add("toAccountId", "Выберите счёт, куда переводить");
                else if (targetId == request.AccountId)
                    errors.Add("toAccountId", "Нельзя перевести на тот же счёт");
                else if (!await db.Accounts.AnyAsync(a => a.Id == targetId && a.UserId == userId, ct))
                    errors.Add("toAccountId", "Счёт назначения не найден");
                toAccountId = request.ToAccountId;
                break;

            case OperationType.Income:
            case OperationType.Expense:
                // Тип категории должен совпадать с типом операции: расход нельзя записать в "Зарплату".
                var expectedCategoryType = request.Type == OperationType.Income
                    ? CategoryType.Income
                    : CategoryType.Expense;

                if (request.CategoryId is not { } catId)
                    errors.Add("categoryId", "Выберите категорию");
                else if (!await db.Categories.AnyAsync(
                             c => c.Id == catId && c.UserId == userId && c.Type == expectedCategoryType, ct))
                    errors.Add("categoryId", "Категория не найдена");
                categoryId = request.CategoryId;
                break;

            default:
                errors.Add("type", "Неизвестный тип операции");
                break;
        }

        // ---------- Описание ----------
        var description = ValidationErrors.NormalizeText(request.Description);
        if (description?.Length > 500)
            errors.Add("description", "Описание длиннее 500 символов");

        // ---------- Дата ----------
        // +1 день запаса: у пользователя может быть другой часовой пояс, и у него "завтра" уже наступило.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (request.Date > today.AddDays(1))
            errors.Add("date", "Дата не может быть в будущем");
        if (request.Date < new DateOnly(2000, 1, 1))
            errors.Add("date", "Слишком старая дата");

        if (errors.HasErrors)
            return null;

        return new ValidOperation(request.Type, request.Amount, request.AccountId, toAccountId, categoryId,
            description, request.Date);
    }
}
