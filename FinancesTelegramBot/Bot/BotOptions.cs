namespace FinancesTelegramBot.Bot;

/// <summary>
/// Настройки бота. Привязываются к секции "Bot" конфигурации (Options pattern).
/// Значения берутся из appsettings.json, user-secrets или переменных окружения
/// (например, Bot__Token) — ASP.NET Core сам объединяет все эти источники.
/// </summary>
public sealed class BotOptions
{
    public const string SectionName = "Bot";

    /// <summary>Токен от @BotFather. Храним ТОЛЬКО в user-secrets / env, не в git.</summary>
    public string Token { get; init; } = string.Empty;

    /// <summary>
    /// Публичный HTTPS-адрес Mini App (например, адрес туннеля ngrok/cloudflared).
    /// Telegram открывает Mini App только по HTTPS.
    /// </summary>
    public string WebAppUrl { get; init; } = string.Empty;
}
