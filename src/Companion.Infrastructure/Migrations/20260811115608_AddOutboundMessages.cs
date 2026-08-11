using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboundMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboundMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboundMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboundMessages_UserId_SentAt",
                table: "OutboundMessages",
                columns: new[] { "UserId", "SentAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboundMessages");
        }
    }
}
