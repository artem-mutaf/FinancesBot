using System.Security.Claims;

namespace FinancesTelegramBot.Auth;

/// <summary>
/// Методы расширения (extension methods): "добавляем" свои методы к чужому классу.
/// Благодаря "this" перед параметром можно писать user.GetUserId() вместо
/// ClaimsPrincipalExtensions.GetUserId(user).
/// </summary>
public static class ClaimsPrincipalExtensions
{
    public static long GetUserId(this ClaimsPrincipal user) =>
        long.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? throw new InvalidOperationException("Пользователь не аутентифицирован"));

    public static string GetFirstName(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Name) ?? string.Empty;

    public static string? GetUsername(this ClaimsPrincipal user) =>
        user.FindFirstValue(TelegramClaimTypes.Username);
}
