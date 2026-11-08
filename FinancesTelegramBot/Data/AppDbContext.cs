using FinancesTelegramBot.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinancesTelegramBot.Data;

/// <summary>
/// DbContext — "окно" в базу данных. Через него читаем и пишем сущности.
/// Каждый DbSet = таблица. EF Core по классам строит схему БД и SQL-запросы.
///
/// Время жизни — Scoped: один экземпляр на HTTP-запрос. DbContext НЕ потокобезопасен,
/// поэтому его нельзя держать в singleton.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Operation> Operations => Set<Operation>();

    /// <summary>
    /// Fluent API: здесь уточняем то, что EF не может угадать по соглашениям (conventions).
    /// Соглашения EF: свойство "Id" = первичный ключ, "UserId" + навигация "User" = внешний ключ и т.д.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ---------- AppUser ----------
        modelBuilder.Entity<AppUser>(e =>
        {
            // Id приходит из Telegram, БД не должна генерировать его сама.
            e.Property(u => u.Id).ValueGeneratedNever();
            e.Property(u => u.FirstName).HasMaxLength(256);
            e.Property(u => u.Username).HasMaxLength(64);
            e.Property(u => u.Currency).HasConversion<string>().HasMaxLength(3);
        });

        // ---------- Account ----------
        modelBuilder.Entity<Account>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(100);

            // Храним enum строкой ("Card"), а не числом (0) — в БД читабельнее,
            // и не сломается, если поменять порядок значений в enum.
            e.Property(a => a.Type).HasConversion<string>().HasMaxLength(20);

            // numeric(18,2): до 16 знаков до запятой и ровно 2 после — копейки.
            e.Property(a => a.InitialBalance).HasPrecision(18, 2);

            // Связь "пользователь 1 — * счета". При удалении пользователя удаляются и его счета.
            e.HasOne(a => a.User)
                .WithMany(u => u.Accounts)
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ---------- Category ----------
        modelBuilder.Entity<Category>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(100);
            e.Property(c => c.Type).HasConversion<string>().HasMaxLength(20);
            // Эмодзи может состоять из нескольких символов (👨‍👩‍👧 — это 5 code points), берём с запасом.
            e.Property(c => c.Icon).HasMaxLength(16);

            e.HasOne(c => c.User)
                .WithMany(u => u.Categories)
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // У одного пользователя не может быть двух категорий расходов "Продукты".
            e.HasIndex(c => new { c.UserId, c.Type, c.Name }).IsUnique();
        });

        // ---------- Operation ----------
        modelBuilder.Entity<Operation>(e =>
        {
            e.Property(o => o.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(o => o.Amount).HasPrecision(18, 2);
            e.Property(o => o.Description).HasMaxLength(500);

            e.HasOne(o => o.User)
                .WithMany(u => u.Operations)
                .HasForeignKey(o => o.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Две связи с ОДНОЙ таблицей Account. Явно говорим, какой внешний ключ
            // к какой навигации относится, — так EF не придётся угадывать.
            //
            // Restrict: нельзя удалить счёт, по которому есть операции
            // (иначе история и балансы молча поломаются).
            e.HasOne(o => o.Account)
                .WithMany()
                .HasForeignKey(o => o.AccountId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(o => o.ToAccount)
                .WithMany()
                .HasForeignKey(o => o.ToAccountId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(o => o.Category)
                .WithMany()
                .HasForeignKey(o => o.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);

            // Индекс под самый частый запрос: "операции пользователя, свежие сверху".
            e.HasIndex(o => new { o.UserId, o.Date });

            // Ограничения на уровне самой БД — последний рубеж защиты от кривых данных,
            // даже если в коде API будет баг.
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Operation_Amount_Positive", "\"Amount\" > 0");
                t.HasCheckConstraint("CK_Operation_Transfer_Target",
                    "(\"Type\" = 'Transfer' AND \"ToAccountId\" IS NOT NULL AND \"CategoryId\" IS NULL " +
                    "AND \"ToAccountId\" <> \"AccountId\") " +
                    "OR (\"Type\" <> 'Transfer' AND \"ToAccountId\" IS NULL AND \"CategoryId\" IS NOT NULL)");
            });
        });
    }
}
