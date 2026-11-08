namespace FinancesTelegramBot.Data.Entities;

/// <summary>Счёт — место, где лежат деньги: карта, наличные, накопления.</summary>
public class Account
{
    /// <summary>Первичный ключ. int + имя "Id" => EF сам сделает его автоинкрементом.</summary>
    public int Id { get; set; }

    // ----- Владелец -----
    /// <summary>Внешний ключ: значение, которое реально хранится в таблице.</summary>
    public long UserId { get; set; }

    /// <summary>
    /// Навигационное свойство: сам объект пользователя. EF заполнит его, если попросить (Include).
    /// "= null!" — говорим компилятору "не будет null, доверься": связь обязательная.
    /// </summary>
    public AppUser User { get; set; } = null!;

    // ----- Данные -----
    public string Name { get; set; } = string.Empty;

    public AccountType Type { get; set; }

    /// <summary>
    /// Сколько денег было на счёте, когда пользователь его завёл.
    /// decimal — точная десятичная арифметика, единственный правильный тип для денег.
    /// </summary>
    public decimal InitialBalance { get; set; }

    public DateTime CreatedAt { get; set; }

    // Текущего баланса здесь НЕТ специально: он вычисляется из операций.
}
