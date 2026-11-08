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
        group.MapPost("/", CreateOperation);
        group.MapDelete("/{id:int}", DeleteOperation);

        return app;
    }

    /// <summary>
    /// GET /api/operations?limit=50 — последние операции, свежие сверху.
    /// limit берётся из query string; "int limit = 50" — значение по умолчанию, если не передали.
    /// </summary>
    private static async Task<List<OperationDto>> GetOperations(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken ct, int limit = 50)
    {
        var userId = principal.GetUserId();
        limit = Math.Clamp(limit, 1, 200); // защита от ?limit=1000000

        return await db.Operations
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.Date)
            .ThenByDescending(o => o.CreatedAt) // внутри одного дня — по времени добавления
            .ThenByDescending(o => o.Id)
            .Take(limit)                        // SQL LIMIT
            .SelectOperationDtos()
            .ToListAsync(ct);
    }

    /// <summary>POST /api/operations — добавить доход, расход или перевод.</summary>
    private static async Task<IResult> CreateOperation(
        CreateOperationRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var errors = new ValidationErrors();

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
        // switch по enum: компилятор подскажет, если появится новый тип, а мы его не обработали.
        int? toAccountId = null;
        int? categoryId = null;

        switch (request.Type)
        {
            case OperationType.Transfer:
                if (request.ToAccountId is not { } targetId) // pattern: "не null, и назови значение targetId"
                {
                    errors.Add("toAccountId", "Выберите счёт, куда переводить");
                }
                else if (targetId == request.AccountId)
                {
                    errors.Add("toAccountId", "Нельзя перевести на тот же счёт");
                }
                else if (!await db.Accounts.AnyAsync(a => a.Id == targetId && a.UserId == userId, ct))
                {
                    errors.Add("toAccountId", "Счёт назначения не найден");
                }
                toAccountId = request.ToAccountId;
                break;

            case OperationType.Income:
            case OperationType.Expense:
                // Тип категории должен совпадать с типом операции: расход нельзя записать в "Зарплату".
                var expectedCategoryType = request.Type == OperationType.Income
                    ? CategoryType.Income
                    : CategoryType.Expense;

                if (request.CategoryId is not { } catId)
                {
                    errors.Add("categoryId", "Выберите категорию");
                }
                else if (!await db.Categories.AnyAsync(
                             c => c.Id == catId && c.UserId == userId && c.Type == expectedCategoryType, ct))
                {
                    errors.Add("categoryId", "Категория не найдена");
                }
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
            return errors.ToResult();

        // ---------- Сохраняем ----------
        var operation = new Operation
        {
            UserId = userId,
            Type = request.Type,
            Amount = request.Amount,
            AccountId = request.AccountId,
            ToAccountId = toAccountId,
            CategoryId = categoryId,
            Description = description,
            Date = request.Date,
            CreatedAt = DateTime.UtcNow,
        };
        db.Operations.Add(operation);
        await db.SaveChangesAsync(ct);

        // Перечитываем через ту же проекцию, чтобы вернуть названия счёта и категории.
        var dto = await db.Operations.Where(o => o.Id == operation.Id).SelectOperationDtos().SingleAsync(ct);
        return Results.Created($"/api/operations/{operation.Id}", dto);
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
}
