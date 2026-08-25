using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Companion.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmotionalSignalEvidenceIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EvidenceEventId",
                table: "EmotionalSignals",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<bool>(
                name: "EvidenceForgotten",
                table: "EmotionalSignals",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceKind",
                table: "EmotionalSignals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "ForgottenAt",
                table: "EmotionalSignals",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EvidenceEventId",
                table: "EmotionalSignals");

            migrationBuilder.DropColumn(
                name: "EvidenceForgotten",
                table: "EmotionalSignals");

            migrationBuilder.DropColumn(
                name: "EvidenceKind",
                table: "EmotionalSignals");

            migrationBuilder.DropColumn(
                name: "ForgottenAt",
                table: "EmotionalSignals");
        }
    }
}
