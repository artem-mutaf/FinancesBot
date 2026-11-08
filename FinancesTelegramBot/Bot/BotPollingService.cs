using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace FinancesTelegramBot.Bot;

/// <summary>
/// Фоновая служба (BackgroundService), которая живёт всё время работы приложения
/// и забирает апдейты у Telegram методом long polling.
///
/// Long polling: мы сами спрашиваем у Telegram "есть новое?", запрос висит до
/// появления апдейта. Удобно для разработки — не нужен публичный адрес.
/// В продакшене обычно переходят на webhook (Telegram сам шлёт POST нам).
/// </summary>
public sealed class BotPollingService(
    ITelegramBotClient bot,
    UpdateHandler handler,
    IOptions<BotOptions> options,
    ILogger<BotPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await bot.GetMe(stoppingToken);
        logger.LogInformation("Бот @{Username} запущен", me.Username);

        await ConfigureMenuButtonAsync(stoppingToken);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message], // пока нужны только сообщения
            DropPendingUpdates = true              // не обрабатывать то, что пришло пока бот был выключен
        };

        // ReceiveAsync крутится в цикле, пока не отменят stoppingToken (остановка приложения).
        await bot.ReceiveAsync(handler, receiverOptions, stoppingToken);
    }

    /// <summary>
    /// Кнопка "меню" слева от поля ввода — тоже открывает Mini App.
    /// </summary>
    private async Task ConfigureMenuButtonAsync(CancellationToken ct)
    {
        var url = options.Value.WebAppUrl;
        if (string.IsNullOrWhiteSpace(url))
            return;

        await bot.SetChatMenuButton(
            menuButton: new MenuButtonWebApp { Text = "Финансы", WebApp = new WebAppInfo { Url = url } },
            cancellationToken: ct);
    }
}
