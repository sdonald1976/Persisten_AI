using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LinkEmotionalSignalToTopic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "EmotionalSignals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Topic",
                table: "EmotionalSignals",
                type: "TEXT",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "EmotionalSignals");

            migrationBuilder.DropColumn(
                name: "Topic",
                table: "EmotionalSignals");
        }
    }
}
