using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeTradingBot.Migrations
{
    /// <inheritdoc />
    public partial class AddPeakEquityToDailyPnL : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PeakEquity",
                table: "DailyPnLs",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PeakEquity",
                table: "DailyPnLs");
        }
    }
}
