using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtlTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskMaxRunMinutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxRunMinutes",
                table: "EtlTasks",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxRunMinutes",
                table: "EtlTasks");
        }
    }
}
