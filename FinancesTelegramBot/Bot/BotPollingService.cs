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
    WebAppUrlProvider webAppUrl,
    ILogger<BotPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var me = await bot.GetMe(stoppingToken);
        logger.LogInformation("Бот @{Username} запущен", me.Username);

        // Если адрес задан в настройках — сразу ставим кнопку меню.
        // С dev-туннелем адрес появится позже, и кнопку обновит WebAppUrlProvider.SetAsync.
        await webAppUrl.UpdateMenuButtonAsync(stoppingToken);

        // Команды в меню бота (кнопка "/" рядом с полем ввода)
        await bot.SetMyCommands(
        [
            new BotCommand { Command = "start", Description = "Начать" },
            new BotCommand { Command = "help", Description = "Как записывать операции" },
        ], cancellationToken: stoppingToken);

        var receiverOptions = new ReceiverOptions
        {
            // Сообщения (быстрый ввод) и нажатия inline-кнопок (выбор категории, отмена)
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery],
            // НЕ выбрасываем то, что пришло, пока бот был выключен: "500 продукты",
            // отправленное в это время, должно записаться (с датой отправки сообщения).
            DropPendingUpdates = false,
        };

        // ReceiveAsync крутится в цикле, пока не отменят stoppingToken (остановка приложения).
        await bot.ReceiveAsync(handler, receiverOptions, stoppingToken);
    }
}
