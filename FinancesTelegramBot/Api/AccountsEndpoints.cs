using System.Security.Claims;
using FinancesTelegramBot.Auth;
using FinancesTelegramBot.Data;
using FinancesTelegramBot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinancesTelegramBot.Api;

public static class AccountsEndpoints
{
    public static IEndpointRouteBuilder MapAccountsEndpoints(this IEndpointRouteBuilder app)
    {
        // MapGroup — общий префикс и общие настройки для группы эндпоинтов.
        var group = app.MapGroup("/api/accounts").RequireAuthorization();

        group.MapGet("/", GetAccounts);
        group.MapPost("/", CreateAccount);
        group.MapPut("/{id:int}", UpdateAccount);   // {id:int} — ограничение маршрута: только число
        group.MapDelete("/{id:int}", DeleteAccount);

        return app;
    }

    /// <summary>GET /api/accounts — счета текущего пользователя с балансом.</summary>
    private static async Task<List<AccountDto>> GetAccounts(
        ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();

        return await db.Accounts
            .Where(a => a.UserId == userId) // ВАЖНО: всегда фильтруем по текущему пользователю!
            .OrderBy(a => a.Id)
            .SelectAccountDtos(db)
            .ToListAsync(ct);
    }

    /// <summary>POST /api/accounts — новый счёт с текущим остатком.</summary>
    private static async Task<IResult> CreateAccount(
        CreateAccountRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var name = ValidateName(request.Name, errors);
        ValidateBalance(request.Balance, errors);
        if (errors.HasErrors)
            return errors.ToResult();

        var account = new Account
        {
            UserId = principal.GetUserId(),
            Name = name!,
            Type = request.Type,
            // У нового счёта операций нет, поэтому начальный остаток = текущему.
            InitialBalance = request.Balance,
            CreatedAt = DateTime.UtcNow,
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct); // после сохранения EF сам запишет в account.Id сгенерированный ключ

        var dto = new AccountDto(account.Id, account.Name, account.Type, account.InitialBalance, account.InitialBalance);

        // 201 Created + заголовок Location — REST-конвенция для создания ресурса.
        return Results.Created($"/api/accounts/{account.Id}", dto);
    }

    /// <summary>
    /// PUT /api/accounts/{id} — переименовать и/или задать текущий остаток.
    ///
    /// Пользователь говорит "на карте сейчас 15 000". Мы храним не текущий баланс,
    /// а начальный остаток, поэтому пересчитываем его:
    ///     баланс = начальный + движение   =>   новый_начальный = желаемый_баланс − движение
    /// где движение = (текущий баланс − текущий начальный) — сумма всех операций по счёту.
    /// История операций при этом не меняется.
    /// </summary>
    private static async Task<IResult> UpdateAccount(
        int id, UpdateAccountRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();

        // Ищем счёт ТОЛЬКО среди счетов пользователя. Чужой счёт => 404, как будто его нет вовсе
        // (не 403: не стоит даже подтверждать, что счёт с таким id существует).
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);
        if (account is null)
            return Results.NotFound();

        var errors = new ValidationErrors();
        var name = ValidateName(request.Name, errors);
        ValidateBalance(request.Balance, errors);
        if (errors.HasErrors)
            return errors.ToResult();

        var current = await db.Accounts.Where(a => a.Id == id).SelectAccountDtos(db).SingleAsync(ct);
        var movement = current.Balance - current.InitialBalance;

        account.Name = name!;
        account.InitialBalance = request.Balance - movement;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new AccountDto(account.Id, account.Name, account.Type, account.InitialBalance,
            request.Balance));
    }

    /// <summary>DELETE /api/accounts/{id} — только если по счёту нет ни одной операции.</summary>
    private static async Task<IResult> DeleteAccount(
        int id, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();

        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);
        if (account is null)
            return Results.NotFound();

        // AnyAsync -> SQL "SELECT EXISTS(...)": не грузим операции, только проверяем, есть ли хоть одна.
        var hasOperations = await db.Operations.AnyAsync(o => o.AccountId == id || o.ToAccountId == id, ct);
        if (hasOperations)
            return Results.Problem(
                detail: "По счёту есть операции. Сначала удали их.",
                statusCode: StatusCodes.Status409Conflict); // 409 — конфликт с текущим состоянием данных

        db.Accounts.Remove(account);
        await db.SaveChangesAsync(ct);

        return Results.NoContent(); // 204 — успешно, тела ответа нет
    }

    // ---------- Валидация ----------

    private static string? ValidateName(string? rawName, ValidationErrors errors)
    {
        var name = ValidationErrors.NormalizeText(rawName);
        if (name is null)
            errors.Add("name", "Введите название счёта");
        else if (name.Length > 100)
            errors.Add("name", "Название длиннее 100 символов");
        return name;
    }

    private static void ValidateBalance(decimal balance, ValidationErrors errors)
    {
        // Отрицательный остаток разрешаем: бывает кредитка или овердрафт.
        if (Math.Abs(balance) >= ValidationErrors.MaxAmount)
            errors.Add("balance", "Слишком большая сумма");
        if (!ValidationErrors.HasAtMostTwoDecimals(balance))
            errors.Add("balance", "Не больше двух знаков после запятой");
    }
}
