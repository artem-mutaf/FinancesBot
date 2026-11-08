namespace FinancesTelegramBot.Data.Entities;

/// <summary>
/// Валюта учёта пользователя. Имена — коды ISO 4217: их понимает
/// Intl.NumberFormat на фронтенде, поэтому символы (₽, ₴, $, €) ставятся автоматически.
/// </summary>
public enum Currency
{
    RUB, // российский рубль
    UAH, // украинская гривна
    USD, // доллар США
    EUR, // евро
    MDL  // молдавский лей
}
