using FinancesTelegramBot.Api;
using FinancesTelegramBot.Data;
using FinancesTelegramBot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinancesTelegramBot.Services;

/// <summary>
/// Результат операции сервиса — один из трёх вариантов.
/// Это приём "размеченное объединение" (discriminated union): базовый record закрыт
/// (private-конструктор), наследники — только вложенные. Вызывающий код разбирает
/// результат через switch и обрабатывает каждый вариант — ничего не забудет.
/// </summary>
public abstract record OperationResult
{
    private OperationResult() { }

    public sealed record Success(OperationDto Operation) : OperationResult;
    public sealed record Invalid(ValidationErrors Errors) : OperationResult;
    public sealed record NotFound : OperationResult;
}

/// <summary>
/// Вся бизнес-логика операций в одном месте: проверка, создание, изменение, удаление.
/// Её используют И API (Mini App), И бот (быстрый ввод в чате) — правила одни и те же,
/// бот не сможет записать то, что API отклонило бы.
///
/// Scoped — потому что зависит от AppDbContext (тоже Scoped).
/// </summary>
public sealed class OperationService(AppDbContext db)
{
    public async Task<OperationResult> CreateAsync(long userId, OperationRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var valid = await ValidateAsync(request, userId, errors, ct);
        if (valid is null)
            return new OperationResult.Invalid(errors);

        var operation = new Operation { UserId = userId, CreatedAt = DateTime.UtcNow };
        Apply(valid, operation);
        db.Operations.Add(operation);
        await db.SaveChangesAsync(ct);

        return new OperationResult.Success(await GetDtoAsync(operation.Id, ct));
    }

    public async Task<OperationResult> UpdateAsync(long userId, int id, OperationRequest request, CancellationToken ct)
    {
        var operation = await db.Operations.SingleOrDefaultAsync(o => o.Id == id && o.UserId == userId, ct);
        if (operation is null)
            return new OperationResult.NotFound();

        var errors = new ValidationErrors();
        var valid = await ValidateAsync(request, userId, errors, ct);
        if (valid is null)
            return new OperationResult.Invalid(errors);

        // CreatedAt НЕ трогаем: это время создания записи, а не изменения.
        Apply(valid, operation);
        await db.SaveChangesAsync(ct); // EF сам сгенерирует UPDATE только по изменённым полям

        return new OperationResult.Success(await GetDtoAsync(id, ct));
    }

    /// <summary>true — удалили; false — операции нет или она чужая.</summary>
    public async Task<bool> DeleteAsync(long userId, int id, CancellationToken ct)
    {
        // ExecuteDeleteAsync — сразу "DELETE FROM ... WHERE ...", без загрузки объекта в память.
        var deleted = await db.Operations
            .Where(o => o.Id == id && o.UserId == userId)
            .ExecuteDeleteAsync(ct);
        return deleted > 0;
    }

    private Task<OperationDto> GetDtoAsync(int id, CancellationToken ct) =>
        db.Operations.Where(o => o.Id == id).SelectOperationDtos().SingleAsync(ct);

    // =====================================================================
    //  Валидация
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
    private async Task<ValidOperation?> ValidateAsync(
        OperationRequest request, long userId, ValidationErrors errors, CancellationToken ct)
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
