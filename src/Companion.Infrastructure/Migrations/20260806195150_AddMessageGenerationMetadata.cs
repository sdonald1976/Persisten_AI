using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageGenerationMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompletionTokens",
                table: "Messages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FinishReason",
                table: "Messages",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GenerationRounds",
                table: "Messages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelUsed",
                table: "Messages",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromptTokens",
                table: "Messages",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Truncated",
                table: "Messages",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletionTokens",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "FinishReason",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "GenerationRounds",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ModelUsed",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "PromptTokens",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "Truncated",
                table: "Messages");
        }
    }
}
