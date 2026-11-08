namespace FinancesTelegramBot.Data.Entities;

/// <summary>Вид операции (движения денег).</summary>
public enum OperationType
{
    Income,   // доход: деньги пришли на счёт
    Expense,  // расход: деньги ушли со счёта
    Transfer  // перевод между своими счетами (например, карта -> накопления)
}
