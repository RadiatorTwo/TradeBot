using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeTradingBot.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DailyPnLs_Date",
                table: "DailyPnLs");

            migrationBuilder.AddColumn<string>(
                name: "AccountId",
                table: "TradingLogs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AccountId",
                table: "Trades",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AccountId",
                table: "DailyPnLs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_TradingLogs_AccountId",
                table: "TradingLogs",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Trades_AccountId",
                table: "Trades",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_DailyPnLs_Date_AccountId",
                table: "DailyPnLs",
                columns: new[] { "Date", "AccountId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TradingLogs_AccountId",
                table: "TradingLogs");

            migrationBuilder.DropIndex(
                name: "IX_Trades_AccountId",
                table: "Trades");

            migrationBuilder.DropIndex(
                name: "IX_DailyPnLs_Date_AccountId",
                table: "DailyPnLs");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "TradingLogs");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Trades");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "DailyPnLs");

            migrationBuilder.CreateIndex(
                name: "IX_DailyPnLs_Date",
                table: "DailyPnLs",
                column: "Date",
                unique: true);
        }
    }
}
