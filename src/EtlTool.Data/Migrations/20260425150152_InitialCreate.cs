using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtlTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProviderType = table.Column<int>(type: "INTEGER", nullable: false),
                    EncryptedConnectionString = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EtlTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SourceConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceSchema = table.Column<string>(type: "TEXT", nullable: false),
                    SourceTable = table.Column<string>(type: "TEXT", nullable: false),
                    TargetConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetSchema = table.Column<string>(type: "TEXT", nullable: false),
                    TargetTable = table.Column<string>(type: "TEXT", nullable: false),
                    WriteMode = table.Column<int>(type: "INTEGER", nullable: false),
                    FilterMode = table.Column<int>(type: "INTEGER", nullable: false),
                    FilterFormJson = table.Column<string>(type: "TEXT", nullable: true),
                    FilterRawSql = table.Column<string>(type: "TEXT", nullable: true),
                    DeleteWhereSameAsFilter = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeleteWhereRawSql = table.Column<string>(type: "TEXT", nullable: true),
                    BatchSize = table.Column<int>(type: "INTEGER", nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EtlTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EtlTaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    TriggerType = table.Column<int>(type: "INTEGER", nullable: false),
                    RowsRead = table.Column<long>(type: "INTEGER", nullable: false),
                    RowsWritten = table.Column<long>(type: "INTEGER", nullable: false),
                    GeneratedReadSql = table.Column<string>(type: "TEXT", nullable: true),
                    GeneratedWriteSql = table.Column<string>(type: "TEXT", nullable: true),
                    SamplePayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunHistories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ColumnMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EtlTaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceColumn = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TargetColumn = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsKey = table.Column<bool>(type: "INTEGER", nullable: false),
                    TransformExpression = table.Column<string>(type: "TEXT", nullable: true),
                    OrderIndex = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColumnMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ColumnMappings_EtlTasks_EtlTaskId",
                        column: x => x.EtlTaskId,
                        principalTable: "EtlTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ColumnMappings_EtlTaskId",
                table: "ColumnMappings",
                column: "EtlTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Connections_Name",
                table: "Connections",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EtlTasks_Name",
                table: "EtlTasks",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunHistories_EtlTaskId_StartedAt",
                table: "RunHistories",
                columns: new[] { "EtlTaskId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ColumnMappings");

            migrationBuilder.DropTable(
                name: "Connections");

            migrationBuilder.DropTable(
                name: "RunHistories");

            migrationBuilder.DropTable(
                name: "EtlTasks");
        }
    }
}
