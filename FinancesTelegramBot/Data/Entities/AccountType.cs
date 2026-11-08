namespace FinancesTelegramBot.Data.Entities;

/// <summary>Вид счёта. enum — набор именованных констант; в БД хранится как число (0, 1, 2).</summary>
public enum AccountType
{
    Card,    // банковская карта
    Cash,    // наличные
    Savings  // накопления
}
