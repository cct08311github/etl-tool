using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtlTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastPasswordChangedAt",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PasswordHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordHistories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PasswordHistories_UserId_CreatedAt",
                table: "PasswordHistories",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PasswordHistories");

            migrationBuilder.DropColumn(
                name: "LastPasswordChangedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                table: "Users");
        }
    }
}
