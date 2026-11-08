using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancesTelegramBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoryIcon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Icon",
                table: "Categories",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // ---------- Миграция ДАННЫХ (дописано вручную) ----------
            // Колонка новая, у уже созданных категорий Icon = NULL. Проставляем иконки
            // стандартным категориям. EF такое не сгенерирует сам — он знает только схему.
            // "Другое" есть в обоих типах, поэтому для неё учитываем Type.
            migrationBuilder.Sql("""
                UPDATE "Categories" SET "Icon" = CASE "Name"
                    WHEN 'Продукты'    THEN '🛒'
                    WHEN 'Транспорт'   THEN '🚌'
                    WHEN 'Кафе'        THEN '☕'
                    WHEN 'Дом'         THEN '🏠'
                    WHEN 'Здоровье'    THEN '💊'
                    WHEN 'Развлечения' THEN '🎬'
                    WHEN 'Зарплата'    THEN '💼'
                    WHEN 'Подарки'     THEN '🎁'
                    WHEN 'Другое'      THEN CASE "Type" WHEN 'Income' THEN '💰' ELSE '📦' END
                END
                WHERE "Icon" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Icon",
                table: "Categories");
        }
    }
}
