using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinancesTelegramBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Users",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                // Исправлено вручную: EF сгенерировал "", но пустая строка не превращается
                // в enum Currency — у существующих пользователей приложение бы упало.
                defaultValue: "RUB");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Users");
        }
    }
}
