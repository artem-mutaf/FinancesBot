namespace FinancesTelegramBot.Services;

/// <summary>
/// "Который сейчас день у пользователя". Сервер живёт в UTC, а пользователь — в своём поясе:
/// в 01:00 по Кишинёву (UTC+2) в UTC ещё вчера. Без этого трата, записанная ночью,
/// попала бы не в тот день.
/// </summary>
public static class UserClock
{
    /// <summary>
    /// Дата в часовом поясе пользователя (или UTC, если пояс неизвестен).
    /// at — момент времени (по умолчанию "сейчас"); бот передаёт время отправки сообщения.
    /// </summary>
    public static DateOnly Today(string? timeZoneId, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        if (timeZoneId is not null && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone))
            now = TimeZoneInfo.ConvertTime(now, zone);
        return DateOnly.FromDateTime(now.DateTime);
    }

    /// <summary>Существует ли такой часовой пояс (данные от клиента проверяем всегда).</summary>
    public static bool IsValid(string? timeZoneId) =>
        timeZoneId is { Length: > 0 and <= 64 } && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _);
}
