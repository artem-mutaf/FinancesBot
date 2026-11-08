using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FinancesTelegramBot.Auth;

/// <summary>
/// Схема аутентификации ASP.NET Core "Telegram".
///
/// Встраиваемся в стандартный механизм: на каждый запрос к защищённому эндпоинту
/// ASP.NET вызывает HandleAuthenticateAsync. Мы читаем заголовок
///     Authorization: tma &lt;initData&gt;
/// проверяем подпись и, если всё ок, создаём ClaimsPrincipal — "кто этот пользователь".
/// Дальше в любом эндпоинте он доступен как HttpContext.User.
///
/// "tma" (Telegram Mini App) — общепринятый префикс, как "Bearer" для JWT.
/// </summary>
public sealed class TelegramAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    TelegramInitDataValidator validator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "Telegram";
    private const string Prefix = "tma ";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;

        // Нет заголовка — "не наш случай". NoResult, а не Fail: вдруг эндпоинт публичный.
        if (header is null || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var initData = header[Prefix.Length..]; // range-синтаксис: строка начиная с индекса Prefix.Length

        if (!validator.TryValidate(initData, out var tgUser) || tgUser is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid Telegram initData"));

        // Claims — "утверждения" о пользователе. NameIdentifier — стандартное место для ID.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, tgUser.Id.ToString()),
            new(ClaimTypes.Name, tgUser.FirstName),
        };
        if (tgUser.Username is not null)
            claims.Add(new Claim(TelegramClaimTypes.Username, tgUser.Username));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public static class TelegramClaimTypes
{
    public const string Username = "telegram_username";
}
