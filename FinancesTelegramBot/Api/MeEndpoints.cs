using System.Security.Claims;
using FinancesTelegramBot.Auth;
using FinancesTelegramBot.Data;
using FinancesTelegramBot.Data.Entities;
using FinancesTelegramBot.Services;

namespace FinancesTelegramBot.Api;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me").RequireAuthorization(); // без валидного initData — 401

        group.MapGet("/", GetMe);
        group.MapPut("/currency", UpdateCurrency);

        return app;
    }

    /// <summary>
    /// GET /api/me — первый запрос Mini App при открытии.
    /// Создаёт пользователя (если его ещё нет) и возвращает профиль.
    ///
    /// Параметры метода ASP.NET заполняет сам:
    ///  ClaimsPrincipal          — текущий пользователь (из нашего TelegramAuthHandler);
    ///  UserProvisioningService  — из DI-контейнера;
    ///  CancellationToken        — отменится, если клиент оборвал запрос.
    /// </summary>
    /// <param name="timeZone">Необязательный, из query string — часовой пояс телефона ("Europe/Chisinau").</param>
    private static async Task<MeDto> GetMe(
        ClaimsPrincipal principal, UserProvisioningService provisioning, CancellationToken ct,
        string? timeZone = null)
    {
        var user = await provisioning.EnsureUserAsync(
            principal.GetUserId(), principal.GetFirstName(), principal.GetUsername(), ct, timeZone);

        return ToDto(user);
    }

    /// <summary>
    /// PUT /api/me/currency — сменить валюту учёта.
    /// Тело запроса (JSON) ASP.NET сам превратит в UpdateCurrencyRequest.
    /// Если прислать неизвестную валюту ("XYZ"), десериализация не пройдёт и
    /// ASP.NET сам вернёт 400 Bad Request — это бесплатная валидация от enum.
    /// </summary>
    private static async Task<IResult> UpdateCurrency(
        UpdateCurrencyRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([principal.GetUserId()], ct);
        if (user is null)
            return Results.NotFound(); // сначала нужно вызвать GET /api/me

        user.Currency = request.Currency;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToDto(user));
    }

    internal static MeDto ToDto(AppUser user) => new(user.Id, user.FirstName, user.Username, user.Currency);
}
