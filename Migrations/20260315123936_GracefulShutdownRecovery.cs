using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeTradingBot.Migrations
{
    /// <inheritdoc />
    public partial class GracefulShutdownRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EngineStateSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    WasRunning = table.Column<bool>(type: "INTEGER", nullable: false),
                    WasPaused = table.Column<bool>(type: "INTEGER", nullable: false),
                    WasKillSwitchActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    OpenPositionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenPositionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ShutdownAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CleanShutdown = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngineStateSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EngineStateSnapshots_AccountId",
                table: "EngineStateSnapshots",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_EngineStateSnapshots_ShutdownAt",
                table: "EngineStateSnapshots",
                column: "ShutdownAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EngineStateSnapshots");
        }
    }
}
