namespace FinancesTelegramBot.Data.Entities;

/// <summary>
/// Пользователь = человек в Telegram.
/// Назван AppUser, а не User, чтобы не путаться с Telegram.Bot.Types.User.
/// </summary>
public class AppUser
{
    /// <summary>
    /// Telegram ID. Это НЕ автоинкремент — значение даёт Telegram.
    /// long, потому что Telegram ID уже вышли за пределы int (2 147 483 647).
    /// </summary>
    public long Id { get; set; }

    // string без "?" — обязательное поле. "= string.Empty", чтобы не было null по умолчанию.
    public string FirstName { get; set; } = string.Empty;

    // string? — может быть null: username в Telegram есть не у всех.
    public string? Username { get; set; }

    /// <summary>
    /// В какой валюте пользователь ведёт учёт. Влияет только на отображение —
    /// суммы не конвертируются (валюта одна на все счета).
    /// </summary>
    public Currency Currency { get; set; } = Currency.RUB;

    /// <summary>Когда пользователь впервые открыл приложение. Всегда в UTC.</summary>
    public DateTime CreatedAt { get; set; }

    // Навигационные коллекции: "у одного пользователя много счетов/категорий/операций".
    // [] — синтаксис collection expression (C# 12), то же самое, что new List<Account>().
    public List<Account> Accounts { get; set; } = [];
    public List<Category> Categories { get; set; } = [];
    public List<Operation> Operations { get; set; } = [];
}
