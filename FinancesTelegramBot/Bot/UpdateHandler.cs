using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace FinancesTelegramBot.Bot;

/// <summary>
/// Обрабатывает входящие апдейты (сообщения, нажатия кнопок и т.д.) от Telegram.
/// Библиотека Telegram.Bot вызывает HandleUpdateAsync для каждого апдейта.
/// </summary>
public sealed class UpdateHandler(
    IOptions<BotOptions> options,
    ILogger<UpdateHandler> logger) : IUpdateHandler
{
    private readonly BotOptions _options = options.Value;

    public async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        // Pattern matching: нас пока интересуют только текстовые сообщения.
        if (update is not { Message: { Text: { } text } message })
            return;

        logger.LogInformation("Сообщение от {UserId}: {Text}", message.From?.Id, text);

        // Команда может прийти как "/start" или "/start@MyBot" — берём часть до '@'.
        var command = text.Split(' ', '@')[0].ToLowerInvariant();

        switch (command)
        {
            case "/start":
                await SendWelcomeAsync(bot, message.Chat.Id, ct);
                break;
            default:
                await bot.SendMessage(message.Chat.Id,
                    "Я понимаю только /start. Учёт ведётся в приложении — нажми кнопку ниже 👇",
                    cancellationToken: ct);
                break;
        }
    }

    private async Task SendWelcomeAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        const string text = "Привет! Я помогу вести учёт личных финансов.\n" +
                            "Открой приложение, чтобы добавлять доходы и расходы.";

        if (string.IsNullOrWhiteSpace(_options.WebAppUrl))
        {
            logger.LogWarning("Bot:WebAppUrl не задан — кнопка Mini App не будет показана");
            await bot.SendMessage(chatId, text, cancellationToken: ct);
            return;
        }

        // Inline-кнопка типа web_app открывает Mini App прямо внутри Telegram.
        var keyboard = new InlineKeyboardMarkup(
            InlineKeyboardButton.WithWebApp("💰 Открыть учёт", new WebAppInfo { Url = _options.WebAppUrl }));

        await bot.SendMessage(chatId, text, replyMarkup: keyboard, cancellationToken: ct);
    }

    public Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, HandleErrorSource source,
        CancellationToken ct)
    {
        // Ошибки сети и т.п. — логируем; polling продолжит работу сам.
        logger.LogError(exception, "Ошибка Telegram ({Source})", source);
        return Task.CompletedTask;
    }
}
