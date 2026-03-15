using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeTradingBot.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositeIndexesAndRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TradingLogs_AccountId_Timestamp",
                table: "TradingLogs",
                columns: new[] { "AccountId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Trades_AccountId_CreatedAt",
                table: "Trades",
                columns: new[] { "AccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Trades_BrokerPositionId_Status",
                table: "Trades",
                columns: new[] { "BrokerPositionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Trades_ClosedAt",
                table: "Trades",
                column: "ClosedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TradingLogs_AccountId_Timestamp",
                table: "TradingLogs");

            migrationBuilder.DropIndex(
                name: "IX_Trades_AccountId_CreatedAt",
                table: "Trades");

            migrationBuilder.DropIndex(
                name: "IX_Trades_BrokerPositionId_Status",
                table: "Trades");

            migrationBuilder.DropIndex(
                name: "IX_Trades_ClosedAt",
                table: "Trades");
        }
    }
}
