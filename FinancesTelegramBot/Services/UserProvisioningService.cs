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
    private static readonly string[] DefaultExpenseCategories =
        ["Продукты", "Транспорт", "Кафе", "Дом", "Здоровье", "Развлечения", "Другое"];

    private static readonly string[] DefaultIncomeCategories =
        ["Зарплата", "Подарки", "Другое"];

    public async Task<AppUser> EnsureUserAsync(long id, string firstName, string? username, CancellationToken ct)
    {
        // FindAsync ищет по первичному ключу (сначала в памяти контекста, потом в БД).
        var user = await db.Users.FindAsync([id], ct);

        if (user is not null)
        {
            // Пользователь мог сменить имя в Telegram — держим данные актуальными.
            // EF сам отслеживает изменения (change tracking) и обновит только изменённые поля.
            user.FirstName = firstName;
            user.Username = username;
            await db.SaveChangesAsync(ct);
            return user;
        }

        var now = DateTime.UtcNow;
        user = new AppUser
        {
            Id = id,
            FirstName = firstName,
            Username = username,
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
                ..DefaultExpenseCategories.Select(n => new Category { Name = n, Type = CategoryType.Expense }),
                ..DefaultIncomeCategories.Select(n => new Category { Name = n, Type = CategoryType.Income }),
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
