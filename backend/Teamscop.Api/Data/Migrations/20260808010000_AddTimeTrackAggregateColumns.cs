using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Teamscop.Api.Data;

#nullable disable

namespace Teamscop.Api.Data.Migrations
{
    /// <summary>
    /// Denormalizes the closed timetrack window onto the row it arrives in, so the §14.1 overview
    /// and §14.2 leaderboard are one <c>SUM ... GROUP BY UserId</c> instead of parsing ~1 440
    /// payloads per staff member per day. Populated at ingest; null on every other event type and
    /// on rows that predate this migration.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260808010000_AddTimeTrackAggregateColumns")]
    public partial class AddTimeTrackAggregateColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SegmentStartedAt",
                table: "agent_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkedSeconds",
                table: "agent_events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdleSeconds",
                table: "agent_events",
                type: "integer",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "IdleSeconds", table: "agent_events");
            migrationBuilder.DropColumn(name: "WorkedSeconds", table: "agent_events");
            migrationBuilder.DropColumn(name: "SegmentStartedAt", table: "agent_events");
        }
    }
}
