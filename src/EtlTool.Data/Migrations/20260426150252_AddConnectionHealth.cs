using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtlTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectionHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastCheckError",
                table: "Connections",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastCheckOk",
                table: "Connections",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCheckedAt",
                table: "Connections",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCheckError",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "LastCheckOk",
                table: "Connections");

            migrationBuilder.DropColumn(
                name: "LastCheckedAt",
                table: "Connections");
        }
    }
}
