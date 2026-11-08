using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace FinancesTelegramBot.Bot;

/// <summary>
/// Точка входа для апдейтов от Telegram. Библиотека вызывает HandleUpdateAsync для каждого апдейта.
///
/// Сам UpdateHandler — Singleton (живёт всё время работы приложения), а AppDbContext — Scoped
/// (один на "единицу работы"). Взять scoped-сервис в singleton напрямую нельзя: контекст БД
/// жил бы вечно, копил бы отслеживаемые объекты и не был бы потокобезопасен.
/// Поэтому на каждый апдейт создаём свой scope — так же, как ASP.NET делает на каждый HTTP-запрос.
/// </summary>
public sealed class UpdateHandler(
    IServiceScopeFactory scopeFactory,
    ILogger<UpdateHandler> logger) : IUpdateHandler
{
    public async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        // using — в конце метода scope уничтожится вместе со всеми scoped-сервисами (и DbContext'ом)
        await using var scope = scopeFactory.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<ChatHandler>();

        try
        {
            switch (update)
            {
                case { Message: { } message }:
                    await chat.HandleMessageAsync(message, ct);
                    break;
                case { CallbackQuery: { } query }:
                    await chat.HandleCallbackAsync(query, ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Фильтр исключений "when": отмену (остановка приложения) не ловим — пусть летит дальше.
            logger.LogError(ex, "Ошибка при обработке апдейта {UpdateId}", update.Id);

            // Пользователь не должен остаться без ответа
            var chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id;
            if (chatId is { } id)
            {
                try
                {
                    await bot.SendMessage(id, "😵 Что-то пошло не так. Попробуй ещё раз.", cancellationToken: ct);
                }
                catch (Exception sendError)
                {
                    logger.LogWarning(sendError, "Не удалось сообщить пользователю об ошибке");
                }
            }
        }
    }

    public Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, HandleErrorSource source,
        CancellationToken ct)
    {
        // Ошибки сети и т.п. — логируем; polling продолжит работу сам.
        logger.LogError(exception, "Ошибка Telegram ({Source})", source);
        return Task.CompletedTask;
    }
}
