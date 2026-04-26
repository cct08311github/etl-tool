using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtlTool.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPostRunSp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PostFailureSp",
                table: "EtlTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostSuccessSp",
                table: "EtlTasks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PostFailureSp",
                table: "EtlTasks");

            migrationBuilder.DropColumn(
                name: "PostSuccessSp",
                table: "EtlTasks");
        }
    }
}
