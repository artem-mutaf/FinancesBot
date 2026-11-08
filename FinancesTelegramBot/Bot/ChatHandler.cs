using System.Globalization;
using System.Text;
using FinancesTelegramBot.Api;
using FinancesTelegramBot.Data;
using FinancesTelegramBot.Data.Entities;
using FinancesTelegramBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace FinancesTelegramBot.Bot;

/// <summary>
/// Обработка сообщений и нажатий кнопок в чате с ботом.
/// Scoped: создаётся заново на каждый апдейт (см. UpdateHandler), поэтому может
/// спокойно использовать AppDbContext и другие scoped-сервисы.
/// </summary>
public sealed class ChatHandler(
    ITelegramBotClient bot,
    AppDbContext db,
    OperationService operations,
    UserProvisioningService provisioning,
    WebAppUrlProvider webAppUrl,
    IMemoryCache cache,
    ILogger<ChatHandler> logger)
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(15);

    private const string Examples =
        "• 500 продукты\n" +
        "• 350 кафе обед с коллегами\n" +
        "• +50000 зарплата\n" +
        "• 1200 такси нал вчера\n" +
        "• 5000 карта > накопления";

    // Формат callback_data у inline-кнопок (Telegram ограничивает его 64 байтами):
    //   u:<operationId>         — отменить (удалить) операцию
    //   c:<key>:<categoryId>    — выбрана категория для отложенной операции
    //   x:<key>                 — отказ от отложенной операции
    private const string UndoPrefix = "u:";
    private const string CategoryPrefix = "c:";
    private const string CancelPrefix = "x:";

    /// <summary>
    /// Операция, для которой бот спросил категорию. Ждёт нажатия кнопки в IMemoryCache
    /// (кэш в памяти процесса с автоудалением по времени). UserId храним, чтобы чужой
    /// человек не смог "нажать" за другого.
    /// </summary>
    private sealed record PendingEntry(
        long UserId, OperationType Type, decimal Amount, int AccountId, string? Description, DateOnly Date);

    // =====================================================================
    //  Сообщения
    // =====================================================================
    public async Task HandleMessageAsync(Message message, CancellationToken ct)
    {
        // Только личные чаты: в группе бот не должен записывать чужие "500 продукты".
        if (message.Chat.Type != ChatType.Private || message.From is not { } from || message.Text is not { } text)
            return;

        // Человек мог написать боту раньше, чем открыл приложение, — создаём его со счетами и категориями.
        var user = await provisioning.EnsureUserAsync(from.Id, from.FirstName, from.Username, ct);
        text = text.Trim();

        logger.LogInformation("Сообщение от {UserId}: {Text}", user.Id, text);

        // message.Date — когда сообщение ОТПРАВЛЕНО (UTC). Если бот был выключен и обрабатывает
        // сообщение позже, операция всё равно попадёт в день отправки.
        var sentAt = new DateTimeOffset(DateTime.SpecifyKind(message.Date, DateTimeKind.Utc));

        if (text.StartsWith('/'))
            await HandleCommandAsync(message.Chat.Id, user, text, ct);
        else
            await HandleQuickEntryAsync(message.Chat.Id, user, text, sentAt, ct);
    }

    private async Task HandleCommandAsync(long chatId, AppUser user, string text, CancellationToken ct)
    {
        // "/start" или "/start@MyBot параметры" -> "/start"
        var command = text.Split(' ', '@')[0].ToLowerInvariant();

        switch (command)
        {
            case "/start":
                await bot.SendMessage(chatId,
                    $"Привет, {user.FirstName}! Я помогу вести учёт личных финансов.\n\n" +
                    $"Записывай траты прямо сюда, в чат:\n{Examples}\n\n" +
                    "Счета, календарь и статистика — в приложении 👇",
                    replyMarkup: AppKeyboard(), cancellationToken: ct);
                break;

            case "/help":
                await bot.SendMessage(chatId,
                    "Как записывать операции:\n\n" +
                    "Сумма + категория (+ счёт, описание, дата — по желанию):\n" +
                    $"{Examples}\n\n" +
                    "• «+» перед суммой — доход, без знака — расход\n" +
                    "  (но «50000 зарплата» — доход и без плюса)\n" +
                    "• счёт можно указать началом названия: «нал», «карт»\n" +
                    "• без счёта — списывается с карты\n" +
                    "• «вчера», «позавчера» — другая дата\n" +
                    "• «1.5к» = 1500\n" +
                    "• если категорию не узнаю — предложу выбрать кнопкой",
                    replyMarkup: AppKeyboard(), cancellationToken: ct);
                break;

            default:
                await bot.SendMessage(chatId, "Не знаю такой команды. Список возможностей — /help",
                    cancellationToken: ct);
                break;
        }
    }

    // =====================================================================
    //  Быстрый ввод: "500 продукты"
    // =====================================================================
    private async Task HandleQuickEntryAsync(long chatId, AppUser user, string text, DateTimeOffset sentAt,
        CancellationToken ct)
    {
        if (!QuickEntryParser.TryParse(text, out var parsed, out var parseError))
        {
            await bot.SendMessage(chatId, $"🤔 {parseError}\n\nПримеры:\n{Examples}", cancellationToken: ct);
            return;
        }

        var accounts = await db.Accounts
            .Where(a => a.UserId == user.Id)
            .OrderBy(a => a.Id)
            .Select(a => new AccountRef(a.Id, a.Name, a.Type))
            .ToListAsync(ct);
        var categories = await db.Categories
            .Where(c => c.UserId == user.Id)
            .OrderBy(c => c.Id)
            .Select(c => new CategoryRef(c.Id, c.Name, c.Icon, c.Type))
            .ToListAsync(ct);

        if (!QuickEntryResolver.TryResolve(parsed, accounts, categories, out var entry, out var resolveError))
        {
            await bot.SendMessage(chatId, $"🤔 {resolveError}", cancellationToken: ct);
            return;
        }

        // "Сегодня" — по часовому поясу пользователя (его сообщает Mini App) на момент отправки
        var date = UserClock.Today(user.TimeZone, sentAt).AddDays(-entry.DaysAgo);

        // Категорию не узнали — спрашиваем кнопками, а операцию откладываем в кэш
        if (entry.Type != OperationType.Transfer && entry.Category is null)
        {
            await AskCategoryAsync(chatId, user, entry, date, categories, ct);
            return;
        }

        var request = new OperationRequest(entry.Type, entry.Amount, entry.Account.Id, entry.ToAccount?.Id,
            entry.Category?.Id, entry.Description, date);
        await CreateAndReplyAsync(chatId, user, request, editMessageId: null, ct);
    }

    private async Task AskCategoryAsync(long chatId, AppUser user, ResolvedQuickEntry entry, DateOnly date,
        List<CategoryRef> categories, CancellationToken ct)
    {
        var categoryType = entry.Type == OperationType.Income ? CategoryType.Income : CategoryType.Expense;
        var options = categories.Where(c => c.Type == categoryType).ToList();

        // Короткий случайный ключ — им кнопки ссылаются на отложенную операцию
        var key = Guid.NewGuid().ToString("N")[..8];
        cache.Set(key, new PendingEntry(user.Id, entry.Type, entry.Amount, entry.Account.Id, entry.Description, date),
            PendingLifetime);

        // Кнопки по две в ряд: Chunk(2) режет список на кусочки по 2 элемента
        var rows = options
            .Select(c => InlineKeyboardButton.WithCallbackData($"{c.Icon ?? "🏷️"} {c.Name}", $"{CategoryPrefix}{key}:{c.Id}"))
            .Chunk(2)
            .Select(pair => pair.ToArray())
            .Append([InlineKeyboardButton.WithCallbackData("✖️ Не записывать", $"{CancelPrefix}{key}")]);

        var what = entry.Type == OperationType.Income ? "дохода" : "расхода";
        var text = new StringBuilder($"Выбери категорию для {what} {Money.Format(entry.Amount, user.Currency)}");
        if (entry.Description is not null)
            text.Append($" «{entry.Description}»");
        text.Append(':');

        await bot.SendMessage(chatId, text.ToString(), replyMarkup: new InlineKeyboardMarkup(rows),
            cancellationToken: ct);
    }

    /// <summary>
    /// Создаёт операцию через общий OperationService и отвечает подтверждением.
    /// editMessageId задан — вместо нового сообщения редактируем старое (с кнопками выбора категории).
    /// </summary>
    private async Task CreateAndReplyAsync(long chatId, AppUser user, OperationRequest request,
        int? editMessageId, CancellationToken ct)
    {
        var result = await operations.CreateAsync(user.Id, request, ct);

        if (result is OperationResult.Invalid invalid)
        {
            await ReplyOrEditAsync(chatId, editMessageId, "⚠️ " + string.Join("\n", invalid.Errors.Messages), null, ct);
            return;
        }
        if (result is not OperationResult.Success { Operation: var op })
            return;

        var text = await BuildConfirmationAsync(user, op, ct);
        var keyboard = new List<InlineKeyboardButton>
        {
            InlineKeyboardButton.WithCallbackData("↩️ Отменить", $"{UndoPrefix}{op.Id}"),
        };
        if (webAppUrl.Url is { } url)
            keyboard.Add(InlineKeyboardButton.WithWebApp("📱 Приложение", new WebAppInfo { Url = url }));

        await ReplyOrEditAsync(chatId, editMessageId, text, new InlineKeyboardMarkup(keyboard), ct);
    }

    /// <summary>Текст подтверждения: что записали и сколько теперь на счёте.</summary>
    private async Task<string> BuildConfirmationAsync(AppUser user, OperationDto op, CancellationToken ct)
    {
        string Fmt(decimal amount) => Money.Format(amount, user.Currency); // локальная функция

        var accountIds = new[] { op.AccountId, op.ToAccountId ?? op.AccountId }.Distinct().ToArray();
        var balances = await db.Accounts
            .Where(a => accountIds.Contains(a.Id)) // Contains -> SQL "IN (...)"
            .SelectAccountDtos(db)
            .ToListAsync(ct);
        var icons = await db.Accounts
            .Where(a => accountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => AccountIcon(a.Type), ct);

        var sb = new StringBuilder();
        switch (op.Type)
        {
            case OperationType.Expense:
                sb.AppendLine($"✅ Расход: {Fmt(op.Amount)}");
                sb.AppendLine($"{op.CategoryIcon ?? "🏷️"} {op.CategoryName} · {icons[op.AccountId]} {op.AccountName}");
                break;
            case OperationType.Income:
                sb.AppendLine($"✅ Доход: +{Fmt(op.Amount)}");
                sb.AppendLine($"{op.CategoryIcon ?? "🏷️"} {op.CategoryName} · {icons[op.AccountId]} {op.AccountName}");
                break;
            case OperationType.Transfer:
                sb.AppendLine($"✅ Перевод: {Fmt(op.Amount)}");
                sb.AppendLine($"{icons[op.AccountId]} {op.AccountName} → {icons[op.ToAccountId!.Value]} {op.ToAccountName}");
                break;
        }

        if (op.Description is not null)
            sb.AppendLine($"💬 {op.Description}");

        var today = UserClock.Today(user.TimeZone);
        if (op.Date != today)
        {
            var dayName = op.Date == today.AddDays(-1) ? "вчера, " : op.Date == today.AddDays(-2) ? "позавчера, " : "";
            sb.AppendLine($"📅 {dayName}{op.Date.ToString("d MMMM", Russian)}");
        }

        sb.AppendLine();
        sb.Append(balances.Count == 1 ? "Остаток: " : "Остатки: ");
        sb.Append(string.Join(" · ", balances.Select(b => $"{icons[b.Id]} {b.Name} — {Fmt(b.Balance)}")));
        return sb.ToString();
    }

    // =====================================================================
    //  Нажатия inline-кнопок
    // =====================================================================
    public async Task HandleCallbackAsync(CallbackQuery query, CancellationToken ct)
    {
        var data = query.Data ?? "";
        var chatId = query.Message?.Chat.Id;
        var messageId = query.Message?.MessageId;

        // Нажатие обязательно "подтвердить" (AnswerCallbackQuery), иначе на кнопке будут вечные часики.
        string? answer = null;

        try
        {
            if (chatId is null || messageId is null)
                return;

            if (data.StartsWith(UndoPrefix) && int.TryParse(data[UndoPrefix.Length..], out var operationId))
            {
                // DeleteAsync сам проверит, что операция принадлежит нажавшему
                var deleted = await operations.DeleteAsync(query.From.Id, operationId, ct);
                answer = deleted ? "Операция удалена" : "Уже удалена";
                if (deleted)
                {
                    // Текст оставляем, дописываем пометку; без replyMarkup кнопки исчезнут
                    await bot.EditMessageText(chatId.Value, messageId.Value,
                        $"{query.Message!.Text}\n\n↩️ Отменено", cancellationToken: ct);
                }
                return;
            }

            if (data.StartsWith(CategoryPrefix))
            {
                // "c:<key>:<categoryId>"
                var parts = data[CategoryPrefix.Length..].Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[1], out var categoryId))
                    return;

                if (!cache.TryGetValue(parts[0], out PendingEntry? pending) || pending is null
                    || pending.UserId != query.From.Id)
                {
                    answer = "Время вышло — отправь сообщение ещё раз";
                    await bot.EditMessageText(chatId.Value, messageId.Value, "⌛ Время выбора истекло",
                        cancellationToken: ct);
                    return;
                }

                cache.Remove(parts[0]); // одна отложенная операция — одно нажатие (защита от двойного клика)

                var user = await db.Users.SingleAsync(u => u.Id == query.From.Id, ct);
                var request = new OperationRequest(pending.Type, pending.Amount, pending.AccountId, null,
                    categoryId, pending.Description, pending.Date);
                await CreateAndReplyAsync(chatId.Value, user, request, editMessageId: messageId, ct);
                return;
            }

            if (data.StartsWith(CancelPrefix))
            {
                cache.Remove(data[CancelPrefix.Length..]);
                await bot.EditMessageText(chatId.Value, messageId.Value, "✖️ Не записано", cancellationToken: ct);
            }
        }
        finally
        {
            // finally — выполнится при любом выходе из try, в том числе через return
            await bot.AnswerCallbackQuery(query.Id, answer, cancellationToken: ct);
        }
    }

    // =====================================================================
    //  Вспомогательное
    // =====================================================================
    private async Task ReplyOrEditAsync(long chatId, int? editMessageId, string text,
        InlineKeyboardMarkup? keyboard, CancellationToken ct)
    {
        if (editMessageId is { } id)
            await bot.EditMessageText(chatId, id, text, replyMarkup: keyboard, cancellationToken: ct);
        else
            await bot.SendMessage(chatId, text, replyMarkup: keyboard, cancellationToken: ct);
    }

    /// <summary>Кнопка, открывающая Mini App (если адрес уже известен).</summary>
    private InlineKeyboardMarkup? AppKeyboard() =>
        webAppUrl.Url is { } url
            ? new InlineKeyboardMarkup(
                InlineKeyboardButton.WithWebApp("💰 Открыть приложение", new WebAppInfo { Url = url }))
            : null;

    private static string AccountIcon(AccountType type) => type switch
    {
        AccountType.Card => "💳",
        AccountType.Cash => "💵",
        AccountType.Savings => "🐷",
        _ => "💰",
    };
}
