using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtlTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRowCountAssertion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MaxExpectedRows",
                table: "EtlTasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MinExpectedRows",
                table: "EtlTasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RowCountPolicy",
                table: "EtlTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxExpectedRows",
                table: "EtlTasks");

            migrationBuilder.DropColumn(
                name: "MinExpectedRows",
                table: "EtlTasks");

            migrationBuilder.DropColumn(
                name: "RowCountPolicy",
                table: "EtlTasks");
        }
    }
}
