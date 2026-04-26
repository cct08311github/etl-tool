using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtlTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEntityChangeHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EntityChangeHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChangedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false),
                    BeforeJson = table.Column<string>(type: "TEXT", nullable: true),
                    AfterJson = table.Column<string>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntityChangeHistories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EntityChangeHistories_ChangedAt",
                table: "EntityChangeHistories",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EntityChangeHistories_EntityType_EntityId_ChangedAt",
                table: "EntityChangeHistories",
                columns: new[] { "EntityType", "EntityId", "ChangedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EntityChangeHistories");
        }
    }
}
