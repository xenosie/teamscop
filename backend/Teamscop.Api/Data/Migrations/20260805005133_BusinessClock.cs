using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Teamscop.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BusinessClock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BusinessAnchorDay",
                table: "companies",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BusinessAnchorHour",
                table: "companies",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BusinessAnchorMinute",
                table: "companies",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BusinessAnchorMonth",
                table: "companies",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BusinessAnchorSecond",
                table: "companies",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BusinessAnchorUtc",
                table: "companies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BusinessAnchorYear",
                table: "companies",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BusinessClockSynchronized",
                table: "companies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BusinessClockUpdatedAt",
                table: "companies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BusinessClockVersion",
                table: "companies",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "BusinessTimeZoneId",
                table: "companies",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "UTC");

            migrationBuilder.AddColumn<long>(
                name: "BusinessClockVersion",
                table: "agent_events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BusinessOccurredAt",
                table: "agent_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BusinessTimeZoneId",
                table: "agent_events",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_events_CompanyId_BusinessOccurredAt",
                table: "agent_events",
                columns: new[] { "CompanyId", "BusinessOccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_agent_events_CompanyId_BusinessOccurredAt",
                table: "agent_events");

            migrationBuilder.DropColumn(
                name: "BusinessAnchorDay",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "BusinessAnchorHour",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "BusinessAnchorMinute",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "BusinessAnchorMonth",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "BusinessAnchorSecond",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "BusinessAnchorUtc",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "BusinessAnchorYear",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "BusinessClockSynchronized",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "BusinessClockUpdatedAt",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "BusinessClockVersion",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "BusinessTimeZoneId",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "BusinessClockVersion",
                table: "agent_events");

            migrationBuilder.DropColumn(
                name: "BusinessOccurredAt",
                table: "agent_events");

            migrationBuilder.DropColumn(
                name: "BusinessTimeZoneId",
                table: "agent_events");
        }
    }
}
