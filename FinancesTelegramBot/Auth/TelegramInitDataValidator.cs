using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FinancesTelegramBot.Bot;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace FinancesTelegramBot.Auth;

/// <summary>Пользователь Telegram в том виде, в каком он приходит внутри initData (JSON).</summary>
public sealed record TelegramUser(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("first_name")] string FirstName,
    [property: JsonPropertyName("username")] string? Username);

/// <summary>
/// Проверяет подпись initData по алгоритму из документации Telegram:
/// https://core.telegram.org/bots/webapps#validating-data-received-via-the-mini-app
///
/// initData выглядит как query string:  query_id=...&amp;user={...json...}&amp;auth_date=1731060000&amp;hash=ab12...
///
/// Алгоритм:
///   1. Берём все пары key=value, КРОМЕ hash, сортируем по ключу, склеиваем через "\n".
///   2. secret_key = HMAC_SHA256(ключ = "WebAppData", данные = токен_бота)
///   3. expected   = HMAC_SHA256(ключ = secret_key,   данные = строка из п.1)
///   4. expected (в hex) должен совпасть с hash. Если совпал — данные пришли от Telegram и не изменены.
/// </summary>
public sealed class TelegramInitDataValidator(IOptions<BotOptions> options)
{
    /// <summary>Сколько живёт initData. Старую подпись не принимаем — защита от повторного использования.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    // Ключ HMAC зависит только от токена — считаем его один раз, а не на каждый запрос.
    private readonly byte[] _secretKey = HMACSHA256.HashData(
        key: Encoding.UTF8.GetBytes("WebAppData"),
        source: Encoding.UTF8.GetBytes(options.Value.Token));

    /// <summary>
    /// Паттерн Try*: возвращает true/false, а результат отдаёт через out-параметр.
    /// Исключения не бросаем — невалидные данные это ожидаемая ситуация, а не авария.
    /// </summary>
    public bool TryValidate(string initData, out TelegramUser? user)
    {
        user = null;

        // ParseQuery разбирает строку и заодно делает URL-декодирование (%7B -> { и т.п.).
        var fields = QueryHelpers.ParseQuery(initData);

        if (!fields.TryGetValue("hash", out var hashValue) || !fields.TryGetValue("user", out var userJson))
            return false;

        // ---- 1. Строка для проверки ----
        var dataCheckString = string.Join('\n', fields
            .Where(f => f.Key != "hash")
            .OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => $"{f.Key}={f.Value}"));

        // ---- 2-3. Считаем ожидаемую подпись ----
        var expectedHash = HMACSHA256.HashData(_secretKey, Encoding.UTF8.GetBytes(dataCheckString));

        // ---- 4. Сравниваем ----
        byte[] receivedHash;
        try
        {
            receivedHash = Convert.FromHexString(hashValue.ToString());
        }
        catch (FormatException)
        {
            return false;
        }

        // FixedTimeEquals сравнивает за одинаковое время независимо от того, где первое отличие.
        // Обычное сравнение выходит на первом несовпавшем байте — по времени ответа
        // атакующий мог бы подбирать подпись побайтно (timing attack).
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, receivedHash))
            return false;

        // ---- Проверяем свежесть ----
        if (!long.TryParse(fields.GetValueOrDefault("auth_date"), out var authDateUnix))
            return false;

        var authDate = DateTimeOffset.FromUnixTimeSeconds(authDateUnix);
        if (DateTimeOffset.UtcNow - authDate > MaxAge)
            return false;

        // ---- Подпись верна: теперь user-JSON можно доверять ----
        user = JsonSerializer.Deserialize<TelegramUser>(userJson.ToString());
        return user is not null;
    }
}
