using System.Security.Claims;
using FinancesTelegramBot.Auth;
using FinancesTelegramBot.Data;
using FinancesTelegramBot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinancesTelegramBot.Api;

public static class CategoriesEndpoints
{
    public static IEndpointRouteBuilder MapCategoriesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/categories").RequireAuthorization();

        group.MapGet("/", GetCategories);
        group.MapPost("/", CreateCategory);
        group.MapPut("/{id:int}", UpdateCategory);
        group.MapDelete("/{id:int}", DeleteCategory);

        return app;
    }

    /// <summary>GET /api/categories — все категории пользователя (доходы и расходы).</summary>
    private static async Task<List<CategoryDto>> GetCategories(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();

        return await db.Categories
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.Id) // порядок создания: новые категории — в конце своего списка
            .Select(c => new CategoryDto(c.Id, c.Name, c.Type, c.Icon))
            .ToListAsync(ct);
    }

    /// <summary>POST /api/categories — новая категория.</summary>
    private static async Task<IResult> CreateCategory(
        CreateCategoryRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var errors = new ValidationErrors();

        var name = ValidateName(request.Name, errors);
        var icon = ValidateIcon(request.Icon, errors);
        if (name is not null && await NameTakenAsync(db, userId, request.Type, name, exceptId: null, ct))
            errors.Add("name", "Такая категория уже есть");
        if (errors.HasErrors)
            return errors.ToResult();

        var category = new Category { UserId = userId, Name = name!, Type = request.Type, Icon = icon };
        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/categories/{category.Id}",
            new CategoryDto(category.Id, category.Name, category.Type, category.Icon));
    }

    /// <summary>PUT /api/categories/{id} — переименовать / сменить иконку.</summary>
    private static async Task<IResult> UpdateCategory(
        int id, UpdateCategoryRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();

        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
        if (category is null)
            return Results.NotFound();

        var errors = new ValidationErrors();
        var name = ValidateName(request.Name, errors);
        var icon = ValidateIcon(request.Icon, errors);
        if (name is not null && await NameTakenAsync(db, userId, category.Type, name, exceptId: id, ct))
            errors.Add("name", "Такая категория уже есть");
        if (errors.HasErrors)
            return errors.ToResult();

        category.Name = name!;
        category.Icon = icon;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new CategoryDto(category.Id, category.Name, category.Type, category.Icon));
    }

    /// <summary>
    /// DELETE /api/categories/{id}?moveToCategoryId=5
    ///
    /// Если по категории есть операции:
    ///   - без moveToCategoryId -> 409 Conflict + operationsCount (фронтенд спросит, куда перенести);
    ///   - с moveToCategoryId   -> переносим операции и удаляем категорию В ОДНОЙ ТРАНЗАКЦИИ.
    /// </summary>
    private static async Task<IResult> DeleteCategory(
        int id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct, int? moveToCategoryId = null)
    {
        var userId = principal.GetUserId();

        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
        if (category is null)
            return Results.NotFound();

        // Без единой категории своего типа нельзя будет добавить ни одного расхода (или дохода).
        var sameTypeCount = await db.Categories.CountAsync(c => c.UserId == userId && c.Type == category.Type, ct);
        if (sameTypeCount <= 1)
            return Results.Problem(
                detail: category.Type == CategoryType.Expense
                    ? "Нельзя удалить последнюю категорию расходов"
                    : "Нельзя удалить последнюю категорию доходов",
                statusCode: StatusCodes.Status409Conflict);

        var operationsCount = await db.Operations.CountAsync(o => o.CategoryId == id, ct);

        if (operationsCount > 0)
        {
            if (moveToCategoryId is not { } targetId)
            {
                // extensions — дополнительные поля в ProblemDetails. Фронтенд прочитает operationsCount.
                return Results.Problem(
                    detail: $"В категории операций: {operationsCount}. Выберите, куда их перенести.",
                    statusCode: StatusCodes.Status409Conflict,
                    extensions: new Dictionary<string, object?> { ["operationsCount"] = operationsCount });
            }

            var targetValid = targetId != id && await db.Categories.AnyAsync(
                c => c.Id == targetId && c.UserId == userId && c.Type == category.Type, ct);
            if (!targetValid)
            {
                var errors = new ValidationErrors();
                errors.Add("moveToCategoryId", "Категория для переноса не найдена");
                return errors.ToResult();
            }

            // ---------- Транзакция ----------
            // ExecuteUpdateAsync выполняется в БД СРАЗУ (не ждёт SaveChanges). Если после переноса
            // удаление категории упадёт — операции окажутся перенесены "наполовину".
            // Транзакция гарантирует: либо выполнится всё, либо ничего (атомарность — "A" в ACID).
            // "await using" — транзакция закроется в конце метода; без CommitAsync она откатится.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Один SQL: UPDATE "Operations" SET "CategoryId" = @target WHERE "CategoryId" = @id
            await db.Operations
                .Where(o => o.CategoryId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.CategoryId, targetId), ct);

            db.Categories.Remove(category);
            await db.SaveChangesAsync(ct);

            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }

        db.Categories.Remove(category);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ---------- Валидация ----------

    private static string? ValidateName(string? rawName, ValidationErrors errors)
    {
        var name = ValidationErrors.NormalizeText(rawName);
        if (name is null)
            errors.Add("name", "Введите название категории");
        else if (name.Length > 100)
            errors.Add("name", "Название длиннее 100 символов");
        return name;
    }

    private static string? ValidateIcon(string? rawIcon, ValidationErrors errors)
    {
        var icon = ValidationErrors.NormalizeText(rawIcon);
        if (icon?.Length > 16)
            errors.Add("icon", "Слишком длинная иконка");
        return icon;
    }

    /// <summary>
    /// Есть ли у пользователя категория с таким же именем и типом (без учёта регистра).
    /// exceptId — при переименовании не считаем саму редактируемую категорию.
    /// ToLower() EF переведёт в SQL-функцию lower().
    /// </summary>
    private static Task<bool> NameTakenAsync(
        AppDbContext db, long userId, CategoryType type, string name, int? exceptId, CancellationToken ct)
    {
        var lowered = name.ToLower();
        return db.Categories.AnyAsync(c =>
            c.UserId == userId && c.Type == type && c.Name.ToLower() == lowered && c.Id != exceptId, ct);
    }
}
