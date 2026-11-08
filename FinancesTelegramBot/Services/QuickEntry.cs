using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using FinancesTelegramBot.Data.Entities;

namespace FinancesTelegramBot.Services;

// =====================================================================
//  Быстрый ввод в чате: "500 продукты", "+50000 зарплата", "5000 карта > накопления".
//
//  Работает в два шага, оба — ЧИСТЫЕ функции (без БД и Telegram), поэтому легко тестируются:
//    1. QuickEntryParser   — текст -> сумма, знак, дата, слова;
//    2. QuickEntryResolver — слова -> категория, счёт, описание (по спискам пользователя).
// =====================================================================

public enum AmountSign { None, Plus, Minus }

/// <summary>Результат разбора текста (ещё без привязки к категориям и счетам).</summary>
public sealed record ParsedQuickEntry(
    decimal Amount,
    AmountSign Sign,
    int DaysAgo,                               // 0 — сегодня, 1 — вчера, 2 — позавчера
    IReadOnlyList<string> Words,               // остальные слова: категория, счёт, описание
    IReadOnlyList<string>? TransferToWords);   // только для перевода: слова справа от ">"

public static partial class QuickEntryParser
{
    /// <summary>
    /// Сумма: необязательный знак, число с копейками через точку или запятую,
    /// необязательный множитель "к"/"тыс" (тысячи) и необязательная валюта.
    ///   500 | +50000 | -300 | 99,90 | 1.5к | 2k | 500р | 100$
    /// </summary>
    [GeneratedRegex(@"^(?<sign>[+\-−])?(?<num>\d{1,13}(?:[.,]\d{1,2})?)(?<mult>к|k|тыс)?\.?(?:р|₽|руб|грн|лей|\$|€)?\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AmountRegex();

    private static readonly Dictionary<string, int> DateWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["сегодня"] = 0,
        ["вчера"] = 1,
        ["позавчера"] = 2,
    };

    /// <summary>
    /// [NotNullWhen(true)] — подсказка компилятору: если метод вернул true, entry точно не null.
    /// Тогда после "if (TryParse(...))" не нужны лишние проверки на null.
    /// </summary>
    public static bool TryParse(string text,
        [NotNullWhen(true)] out ParsedQuickEntry? entry,
        [NotNullWhen(false)] out string? error)
    {
        entry = null;
        error = null;

        // Стрелку перевода отделяем пробелами, чтобы "карта>накопления" тоже разобралось.
        var normalized = text.Replace("->", ">").Replace("→", ">").Replace(">", " > ");
        var tokens = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();

        // ---------- Сумма: первое или последнее слово ----------
        Match? match = null;
        foreach (var index in new[] { 0, tokens.Count - 1 }.Distinct())
        {
            if (index < 0) continue;
            var m = AmountRegex().Match(tokens[index]);
            if (!m.Success) continue;
            match = m;
            tokens.RemoveAt(index);
            break;
        }

        if (match is null)
        {
            error = "Не нашёл сумму.";
            return false;
        }

        // InvariantCulture: разделитель — точка, независимо от языка системы.
        var amount = decimal.Parse(match.Groups["num"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        if (match.Groups["mult"].Success)
            amount *= 1000;
        if (amount <= 0)
        {
            error = "Сумма должна быть больше нуля.";
            return false;
        }

        var sign = match.Groups["sign"].Value switch
        {
            "" => AmountSign.None,
            "+" => AmountSign.Plus,
            _ => AmountSign.Minus, // "-" или "−" (типографский минус)
        };

        // ---------- Дата: "вчера", "позавчера", "сегодня" — где угодно в тексте ----------
        var daysAgo = 0;
        for (var i = tokens.Count - 1; i >= 0; i--) // с конца — чтобы удаление не сдвигало непройденные индексы
        {
            if (DateWords.TryGetValue(tokens[i], out var days))
            {
                daysAgo = days;
                tokens.RemoveAt(i);
            }
        }

        // ---------- Перевод: "5000 карта > накопления" ----------
        var arrow = tokens.IndexOf(">");
        if (arrow >= 0)
        {
            if (sign != AmountSign.None)
            {
                error = "У перевода не ставь + или −.";
                return false;
            }

            var left = tokens.GetRange(0, arrow);
            var right = tokens.Skip(arrow + 1).Where(t => t != ">").ToList();
            if (left.Count == 0 || right.Count == 0)
            {
                error = "Для перевода укажи оба счёта, например: 5000 карта > накопления";
                return false;
            }

            entry = new ParsedQuickEntry(amount, sign, daysAgo, left, right);
            return true;
        }

        entry = new ParsedQuickEntry(amount, sign, daysAgo, tokens, null);
        return true;
    }
}

// ---------- Справочники пользователя в упрощённом виде (то, что нужно для сопоставления) ----------
public sealed record AccountRef(int Id, string Name, AccountType Type);
public sealed record CategoryRef(int Id, string Name, string? Icon, CategoryType Type);

/// <summary>Окончательно разобранная операция. Category == null у расхода/дохода — "спросить категорию".</summary>
public sealed record ResolvedQuickEntry(
    OperationType Type,
    decimal Amount,
    AccountRef Account,
    AccountRef? ToAccount,
    CategoryRef? Category,
    string? Description,
    int DaysAgo);

public static class QuickEntryResolver
{
    public static bool TryResolve(ParsedQuickEntry parsed,
        IReadOnlyList<AccountRef> accounts, IReadOnlyList<CategoryRef> categories,
        [NotNullWhen(true)] out ResolvedQuickEntry? entry,
        [NotNullWhen(false)] out string? error)
    {
        entry = null;
        error = null;

        if (accounts.Count == 0)
        {
            error = "У тебя нет ни одного счёта — создай его в приложении.";
            return false;
        }

        var words = parsed.Words.ToList();

        // ---------- Перевод ----------
        if (parsed.TransferToWords is { } rightWords)
        {
            var right = rightWords.ToList();
            var from = TakeBestMatch(words, accounts, a => a.Name);
            var to = TakeBestMatch(right, accounts, a => a.Name);

            if (from is null)
            {
                error = $"Не нашёл счёт «{string.Join(' ', parsed.Words)}».";
                return false;
            }
            if (to is null)
            {
                error = $"Не нашёл счёт «{string.Join(' ', rightWords)}».";
                return false;
            }

            // Оставшиеся слова с обеих сторон — описание
            entry = new ResolvedQuickEntry(OperationType.Transfer, parsed.Amount, from, to, null,
                JoinDescription([.. words, .. right]), parsed.DaysAgo);
            return true;
        }

        // ---------- Доход / расход ----------
        // "+" — ищем только среди доходов, "−" — только среди расходов,
        // без знака — среди всех (так "50000 зарплата" станет доходом и без плюса).
        var candidates = parsed.Sign switch
        {
            AmountSign.Plus => categories.Where(c => c.Type == CategoryType.Income),
            AmountSign.Minus => categories.Where(c => c.Type == CategoryType.Expense),
            // При равенстве побеждает тот, кто раньше в списке — ставим расходы первыми
            // ("другое" есть в обоих типах, а траты записывают чаще).
            _ => categories.OrderBy(c => c.Type == CategoryType.Expense ? 0 : 1),
        };

        var category = TakeBestMatch(words, candidates.ToList(), c => c.Name);
        var account = TakeBestMatch(words, accounts, a => a.Name) ?? DefaultAccount(accounts);

        var isIncome = category?.Type == CategoryType.Income || (category is null && parsed.Sign == AmountSign.Plus);

        entry = new ResolvedQuickEntry(
            isIncome ? OperationType.Income : OperationType.Expense,
            parsed.Amount, account, null, category, JoinDescription(words), parsed.DaysAgo);
        return true;
    }

    /// <summary>Счёт по умолчанию — первая карта, а если карт нет — первый счёт.</summary>
    private static AccountRef DefaultAccount(IReadOnlyList<AccountRef> accounts) =>
        accounts.FirstOrDefault(a => a.Type == AccountType.Card) ?? accounts[0];

    private static string? JoinDescription(List<string> words) =>
        words.Count == 0 ? null : string.Join(' ', words);

    /// <summary>
    /// Ищет среди слов то, что лучше всего похоже на название одного из items.
    /// Найденное слово УДАЛЯЕТ из списка (оно "потрачено" и не попадёт в описание).
    /// Generic-метод: работает и для счетов, и для категорий — нужно лишь сказать, где взять имя.
    /// </summary>
    private static T? TakeBestMatch<T>(List<string> words, IReadOnlyList<T> items, Func<T, string> getName)
        where T : class
    {
        T? best = null;
        int bestScore = 0, bestWordIndex = -1;

        for (var w = 0; w < words.Count; w++)
        {
            foreach (var item in items)
            {
                var score = MatchScore(words[w], getName(item));
                if (score > bestScore) // строго больше: при равенстве остаётся найденный раньше
                {
                    (best, bestScore, bestWordIndex) = (item, score, w); // присваивание через кортеж
                }
            }
        }

        if (best is not null)
            words.RemoveAt(bestWordIndex);
        return best;
    }

    /// <summary>
    /// Насколько слово похоже на название (0 — не похоже). Сравниваем с каждым словом названия:
    ///   "продукты" = "Продукты"      -> точное совпадение
    ///   "прод", "нал"                -> начало названия (от 3 букв)
    ///   "продуктов", "кафешка"       -> общий корень (от 4 букв), разные окончания
    /// </summary>
    internal static int MatchScore(string word, string name)
    {
        var w = Normalize(word);
        if (w.Length == 0)
            return 0;

        var best = 0;
        foreach (var part in Normalize(name).Split(' ', '-'))
        {
            if (part.Length == 0) continue;

            int score;
            if (w == part)
                score = 100;
            else if (w.Length >= 3 && part.StartsWith(w))
                score = 50 + w.Length;
            else if (CommonPrefixLength(w, part) is var prefix && prefix >= 4 && prefix >= part.Length - 2)
                score = 40 + prefix;
            else if (part.Length >= 3 && w.StartsWith(part))
                score = 30 + part.Length;
            else
                score = 0;

            best = Math.Max(best, score);
        }
        return best;
    }

    /// <summary>Регистр и "ё" не важны; знаки препинания по краям — убираем.</summary>
    private static string Normalize(string s) =>
        s.Trim().Trim(',', '.', '!', '?', ';', ':', '«', '»', '"').ToLowerInvariant().Replace('ё', 'е');

    private static int CommonPrefixLength(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return i;
    }
}
