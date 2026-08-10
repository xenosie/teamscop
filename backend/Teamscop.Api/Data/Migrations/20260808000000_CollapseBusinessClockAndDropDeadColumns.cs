using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Teamscop.Api.Data;

#nullable disable

namespace Teamscop.Api.Data.Migrations
{
    /// <summary>
    /// §8.4 collapses the business clock to a single timezone id, §1.7 removes employee
    /// lifecycle (disable / delete / session revocation), and four company columns were
    /// written but never read.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260808000000_CollapseBusinessClockAndDropDeadColumns")]
    public partial class CollapseBusinessClockAndDropDeadColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // §1.7 — no employee lifecycle, therefore no revocation surface.
            migrationBuilder.DropColumn(name: "SessionVersion", table: "users");
            migrationBuilder.DropColumn(name: "IsDisabled", table: "users");

            // §8.4 — the anchor / synchronisation concept is deleted; BusinessTimeZoneId survives.
            migrationBuilder.DropColumn(name: "BusinessAnchorDay", table: "companies");
            migrationBuilder.DropColumn(name: "BusinessAnchorHour", table: "companies");
            migrationBuilder.DropColumn(name: "BusinessAnchorMinute", table: "companies");
            migrationBuilder.DropColumn(name: "BusinessAnchorMonth", table: "companies");
            migrationBuilder.DropColumn(name: "BusinessAnchorSecond", table: "companies");
            migrationBuilder.DropColumn(name: "BusinessAnchorUtc", table: "companies");
            migrationBuilder.DropColumn(name: "BusinessAnchorYear", table: "companies");
            migrationBuilder.DropColumn(name: "BusinessClockSynchronized", table: "companies");
            migrationBuilder.DropColumn(name: "BusinessClockUpdatedAt", table: "companies");
            migrationBuilder.DropColumn(name: "BusinessClockVersion", table: "companies");

            // Written, never read.
            migrationBuilder.DropColumn(name: "TokenVersion", table: "companies");
            migrationBuilder.DropColumn(name: "UninstallTotpEnabled", table: "companies");
            migrationBuilder.DropColumn(name: "UninstallTotpEnrolledAt", table: "companies");
            migrationBuilder.DropColumn(name: "UninstallTotpSecret", table: "companies");

            // B11 — BusinessOccurredAt stored a wall clock labelled UTC. Company-local time is
            // now derived from OccurredAt (a real instant) + companies.BusinessTimeZoneId at
            // query time, so the three columns and their index are redundant.
            migrationBuilder.DropIndex(
                name: "IX_agent_events_CompanyId_BusinessOccurredAt",
                table: "agent_events");
            migrationBuilder.DropColumn(name: "BusinessClockVersion", table: "agent_events");
            migrationBuilder.DropColumn(name: "BusinessOccurredAt", table: "agent_events");
            migrationBuilder.DropColumn(name: "BusinessTimeZoneId", table: "agent_events");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
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

            migrationBuilder.AddColumn<int>(
                name: "TokenVersion",
                table: "companies",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "UninstallTotpEnabled",
                table: "companies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UninstallTotpEnrolledAt",
                table: "companies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UninstallTotpSecret",
                table: "companies",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

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
    }
}
