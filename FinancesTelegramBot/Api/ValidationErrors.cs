namespace FinancesTelegramBot.Api;

/// <summary>
/// Маленький сборщик ошибок валидации. Копим ВСЕ ошибки, а не падаем на первой —
/// так пользователь сразу видит, что исправить.
///
/// Результат — стандартный ответ 400 в формате ProblemDetails (RFC 9457):
/// { "title": "...", "errors": { "amount": ["Сумма должна быть больше нуля"] } }
/// </summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new();

    public bool HasErrors => _errors.Count > 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
            _errors[field] = list = [];
        list.Add(message);
    }

    public IResult ToResult() =>
        Results.ValidationProblem(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));

    // ---------- Общие проверки ----------

    /// <summary>Максимум, который влезает в numeric(18,2) с запасом.</summary>
    public const decimal MaxAmount = 1_000_000_000_000m; // m — суффикс литерала decimal

    public static string? NormalizeText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>Проверяет, что у суммы не больше двух знаков после запятой (копейки).</summary>
    public static bool HasAtMostTwoDecimals(decimal value) => decimal.Round(value, 2) == value;
}
