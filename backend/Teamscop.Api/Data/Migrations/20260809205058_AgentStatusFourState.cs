using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Teamscop.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentStatusFourState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentStatus",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentStatusReason",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AgentStatusSince",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AppDefectSinceAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppServiceState",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAppReportAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCaptureReason",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCaptureState",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastMissingComponents",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UninstalledAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentStatus",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AgentStatusReason",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AgentStatusSince",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AppDefectSinceAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AppServiceState",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastAppReportAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastCaptureReason",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastCaptureState",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastMissingComponents",
                table: "users");

            migrationBuilder.DropColumn(
                name: "UninstalledAt",
                table: "users");
        }
    }
}
