using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace FinancesTelegramBot.Bot;

/// <summary>
/// Хранит ТЕКУЩИЙ адрес Mini App. Раньше адрес читался из настроек один раз при старте;
/// теперь он может поменяться на ходу (dev-туннель переподключился и получил новый адрес).
///
/// Singleton: один экземпляр на всё приложение, его читают бот и служба туннеля.
/// </summary>
public sealed class WebAppUrlProvider(IOptions<BotOptions> options, ITelegramBotClient bot,
    ILogger<WebAppUrlProvider> logger)
{
    // volatile: значение пишет один поток (служба туннеля), а читают другие (обработчик бота).
    // volatile гарантирует, что читатели сразу увидят новое значение, а не закэшированное старое.
    private volatile string? _url =
        string.IsNullOrWhiteSpace(options.Value.WebAppUrl) ? null : options.Value.WebAppUrl;

    public string? Url => _url;

    /// <summary>Запомнить новый адрес и сразу перенастроить кнопку меню в боте.</summary>
    public async Task SetAsync(string url, CancellationToken ct)
    {
        _url = url;
        logger.LogInformation("Адрес Mini App: {Url}", url);
        await UpdateMenuButtonAsync(ct);
    }

    /// <summary>Кнопка "меню" слева от поля ввода в чате с ботом — открывает Mini App.</summary>
    public async Task UpdateMenuButtonAsync(CancellationToken ct)
    {
        if (_url is not { } url)
            return;

        await bot.SetChatMenuButton(
            menuButton: new MenuButtonWebApp { Text = "Финансы", WebApp = new WebAppInfo { Url = url } },
            cancellationToken: ct);
    }
}
