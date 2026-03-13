using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeTradingBot.Migrations
{
    /// <inheritdoc />
    public partial class AddPositionSyncFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAt",
                table: "Trades",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RealizedPnL",
                table: "Trades",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Side",
                table: "Positions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "RealizedPnL",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "Side",
                table: "Positions");
        }
    }
}
