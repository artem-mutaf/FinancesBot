using FinancesTelegramBot.Data;
using FinancesTelegramBot.Data.Entities;

namespace FinancesTelegramBot.Api;

/// <summary>
/// Переиспользуемые проекции (Select) из сущностей в DTO.
///
/// Это методы расширения для IQueryable — они НЕ выполняют запрос, а лишь достраивают
/// "дерево выражения" (Expression Tree). SQL сгенерируется и выполнится позже,
/// когда вызовут ToListAsync / SingleOrDefaultAsync. Поэтому их можно комбинировать:
///     db.Accounts.Where(...).SelectAccountDtos(db).OrderBy(...).ToListAsync()
/// — и всё это превратится в ОДИН SQL-запрос.
/// </summary>
public static class Queries
{
    /// <summary>
    /// Счёт + вычисленный баланс:
    ///   начальный остаток
    ///   + Σ по операциям, где счёт — источник: доход (+), расход и перевод "отсюда" (−)
    ///   + Σ по переводам, где счёт — получатель (+)
    /// Считает сама PostgreSQL (подзапросы SUM), в C# ничего не перебирается.
    /// </summary>
    public static IQueryable<AccountDto> SelectAccountDtos(this IQueryable<Account> accounts, AppDbContext db) =>
        accounts.Select(a => new AccountDto(
            a.Id,
            a.Name,
            a.Type,
            a.InitialBalance,
            a.InitialBalance
            + db.Operations
                .Where(o => o.AccountId == a.Id)
                .Sum(o => o.Type == OperationType.Income ? o.Amount : -o.Amount)
            + db.Operations
                .Where(o => o.ToAccountId == a.Id)
                .Sum(o => o.Amount)));

    /// <summary>
    /// Операция + названия счёта и категории. Обращение к навигациям (o.Account.Name)
    /// внутри Select EF превратит в LEFT JOIN — Include тут не нужен.
    /// "?." для необязательных навигаций: у дохода нет ToAccount, у перевода нет Category.
    /// </summary>
    public static IQueryable<OperationDto> SelectOperationDtos(this IQueryable<Operation> operations) =>
        operations.Select(o => new OperationDto(
            o.Id,
            o.Type,
            o.Amount,
            o.AccountId,
            o.Account.Name,
            o.ToAccountId,
            o.ToAccount != null ? o.ToAccount.Name : null,
            o.CategoryId,
            o.Category != null ? o.Category.Name : null,
            o.Description,
            o.Date));
}
