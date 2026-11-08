using FinancesTelegramBot.Data;
using FinancesTelegramBot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinancesTelegramBot.Services;

/// <summary>
/// "Провижининг" — подготовка пользователя: при первом входе создаём его запись,
/// три счёта и набор категорий по умолчанию. При повторных входах — обновляем имя.
/// </summary>
public sealed class UserProvisioningService(AppDbContext db, ILogger<UserProvisioningService> logger)
{
    // Массив кортежей (tuple): каждая запись — пара (название, иконка).
    // Именованные элементы кортежа позволяют писать c.Name вместо c.Item1.
    private static readonly (string Name, string Icon)[] DefaultExpenseCategories =
    [
        ("Продукты", "🛒"), ("Транспорт", "🚌"), ("Кафе", "☕"), ("Дом", "🏠"),
        ("Здоровье", "💊"), ("Развлечения", "🎬"), ("Другое", "📦"),
    ];

    private static readonly (string Name, string Icon)[] DefaultIncomeCategories =
    [
        ("Зарплата", "💼"), ("Подарки", "🎁"), ("Другое", "💰"),
    ];

    /// <param name="timeZone">
    /// Часовой пояс от Mini App. null — не трогать сохранённый (бот его не знает).
    /// </param>
    public async Task<AppUser> EnsureUserAsync(long id, string firstName, string? username, CancellationToken ct,
        string? timeZone = null)
    {
        var validTimeZone = UserClock.IsValid(timeZone) ? timeZone : null;

        // FindAsync ищет по первичному ключу (сначала в памяти контекста, потом в БД).
        var user = await db.Users.FindAsync([id], ct);

        if (user is not null)
        {
            // Пользователь мог сменить имя в Telegram — держим данные актуальными.
            // EF сам отслеживает изменения (change tracking) и обновит только изменённые поля;
            // если ничего не поменялось, SaveChanges вообще не пойдёт в базу.
            user.FirstName = firstName;
            user.Username = username;
            if (validTimeZone is not null)
                user.TimeZone = validTimeZone;
            await db.SaveChangesAsync(ct);
            return user;
        }

        var now = DateTime.UtcNow;
        user = new AppUser
        {
            Id = id,
            FirstName = firstName,
            Username = username,
            TimeZone = validTimeZone,
            CreatedAt = now,

            // Связанные объекты добавляем прямо в коллекции — EF сам проставит UserId
            // и вставит всё одним SaveChanges (в одной транзакции БД).
            Accounts =
            [
                new Account { Name = "Карта", Type = AccountType.Card, CreatedAt = now },
                new Account { Name = "Наличные", Type = AccountType.Cash, CreatedAt = now },
                new Account { Name = "Накопления", Type = AccountType.Savings, CreatedAt = now },
            ],

            // LINQ Select превращает массив строк в объекты Category; .. — spread-оператор,
            // "развернуть элементы последовательности внутрь списка".
            Categories =
            [
                ..DefaultExpenseCategories.Select(c =>
                    new Category { Name = c.Name, Icon = c.Icon, Type = CategoryType.Expense }),
                ..DefaultIncomeCategories.Select(c =>
                    new Category { Name = c.Name, Icon = c.Icon, Type = CategoryType.Income }),
            ],
        };

        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Новый пользователь {UserId} ({FirstName})", id, firstName);
            return user;
        }
        catch (DbUpdateException)
        {
            // Гонка: два запроса одновременно решили, что пользователя нет, и оба вставляют.
            // Один успел первым — второй получит ошибку первичного ключа. Это не страшно:
            // сбрасываем свои несохранённые объекты и берём уже созданного пользователя.
            db.ChangeTracker.Clear();
            return await db.Users.SingleAsync(u => u.Id == id, ct);
        }
    }
}
