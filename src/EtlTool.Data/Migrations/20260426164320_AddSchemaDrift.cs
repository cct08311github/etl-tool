using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtlTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSchemaDrift : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SchemaDriftPolicy",
                table: "EtlTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "SchemaSnapshotAt",
                table: "EtlTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceSchemaSnapshotJson",
                table: "EtlTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetSchemaSnapshotJson",
                table: "EtlTasks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SchemaDriftPolicy",
                table: "EtlTasks");

            migrationBuilder.DropColumn(
                name: "SchemaSnapshotAt",
                table: "EtlTasks");

            migrationBuilder.DropColumn(
                name: "SourceSchemaSnapshotJson",
                table: "EtlTasks");

            migrationBuilder.DropColumn(
                name: "TargetSchemaSnapshotJson",
                table: "EtlTasks");
        }
    }
}
