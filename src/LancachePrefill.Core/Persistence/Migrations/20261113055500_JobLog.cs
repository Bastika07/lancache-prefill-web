using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancachePrefill.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JobLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LogJson",
                table: "JobRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogJson",
                table: "JobRecords");
        }
    }
}
