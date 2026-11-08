namespace FinancesTelegramBot.Data.Entities;

/// <summary>Категория дохода или расхода: "Продукты", "Транспорт", "Зарплата"...</summary>
public class Category
{
    public int Id { get; set; }

    public long UserId { get; set; }
    public AppUser User { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    public CategoryType Type { get; set; }

    /// <summary>Эмодзи для отображения ("🛒"). Необязательное — без него фронтенд покажет 🏷️.</summary>
    public string? Icon { get; set; }
}
