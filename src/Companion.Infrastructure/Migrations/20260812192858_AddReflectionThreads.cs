using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReflectionThreads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ContinuesReflectionId",
                table: "Reflections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ThreadId",
                table: "Reflections",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<bool>(
                name: "ThreadSettled",
                table: "Reflections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Reflections_UserId_ThreadId",
                table: "Reflections",
                columns: new[] { "UserId", "ThreadId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reflections_UserId_ThreadId",
                table: "Reflections");

            migrationBuilder.DropColumn(
                name: "ContinuesReflectionId",
                table: "Reflections");

            migrationBuilder.DropColumn(
                name: "ThreadId",
                table: "Reflections");

            migrationBuilder.DropColumn(
                name: "ThreadSettled",
                table: "Reflections");
        }
    }
}
