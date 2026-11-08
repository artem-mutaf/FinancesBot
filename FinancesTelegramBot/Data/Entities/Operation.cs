namespace FinancesTelegramBot.Data.Entities;

/// <summary>
/// Операция — любое движение денег: доход, расход или перевод.
/// Названа Operation, а не Transaction, чтобы не путать с транзакциями БД.
///
/// Правила (проверяются в API, не здесь):
///   Income / Expense  -> CategoryId задан,  ToAccountId = null
///   Transfer          -> ToAccountId задан, CategoryId = null, ToAccountId != AccountId
/// </summary>
public class Operation
{
    public int Id { get; set; }

    public long UserId { get; set; }
    public AppUser User { get; set; } = null!;

    public OperationType Type { get; set; }

    /// <summary>Сумма всегда положительная. Направление определяет Type, а не знак.</summary>
    public decimal Amount { get; set; }

    // ----- Счёт, с которым связана операция -----
    // Для расхода и перевода — откуда списали, для дохода — куда пришло.
    public int AccountId { get; set; }
    public Account Account { get; set; } = null!;

    // ----- Счёт назначения (только для перевода) -----
    // int? и Account? — необязательные: у дохода/расхода их нет.
    public int? ToAccountId { get; set; }
    public Account? ToAccount { get; set; }

    // ----- Категория (только для дохода/расхода) -----
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    /// <summary>Описание платежа: "Пятёрочка", "Обед с коллегами". Необязательное.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Дата операции, которую выбрал пользователь (можно внести вчерашнюю трату).
    /// DateOnly — только дата без времени: для учёта финансов время не нужно,
    /// и нет проблем с часовыми поясами.
    /// </summary>
    public DateOnly Date { get; set; }

    /// <summary>Когда запись реально создана (UTC). Нужно для сортировки операций внутри одного дня.</summary>
    public DateTime CreatedAt { get; set; }
}
