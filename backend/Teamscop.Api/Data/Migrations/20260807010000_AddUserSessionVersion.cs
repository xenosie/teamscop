using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Teamscop.Api.Data;

#nullable disable

namespace Teamscop.Api.Data.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260807010000_AddUserSessionVersion")]
    public partial class AddUserSessionVersion : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SessionVersion",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<bool>(
                name: "IsDisabled",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "SessionVersion", table: "users");
            migrationBuilder.DropColumn(name: "IsDisabled", table: "users");
        }
    }
}
