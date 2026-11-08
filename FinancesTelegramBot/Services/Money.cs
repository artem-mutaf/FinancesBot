using System.Globalization;
using FinancesTelegramBot.Data.Entities;

namespace FinancesTelegramBot.Services;

/// <summary>Форматирование сумм для сообщений бота: 1500.5 -> "1 500,5 ₽".</summary>
public static class Money
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static string Format(decimal amount, Currency currency)
    {
        var symbol = currency switch
        {
            Currency.RUB => "₽",
            Currency.UAH => "₴",
            Currency.USD => "$",
            Currency.EUR => "€",
            Currency.MDL => "L",
            _ => currency.ToString(),
        };
        // "#,0.##": разделитель тысяч (в ru-RU это неразрывный пробел), копейки — только если есть
        return $"{amount.ToString("#,0.##", Russian)} {symbol}";
    }
}
