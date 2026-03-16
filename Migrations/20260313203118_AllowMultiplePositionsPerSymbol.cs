using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeTradingBot.Migrations
{
    /// <inheritdoc />
    public partial class AllowMultiplePositionsPerSymbol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Positions_Symbol",
                table: "Positions");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_BrokerPositionId",
                table: "Positions",
                column: "BrokerPositionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Positions_Symbol",
                table: "Positions",
                column: "Symbol");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Positions_BrokerPositionId",
                table: "Positions");

            migrationBuilder.DropIndex(
                name: "IX_Positions_Symbol",
                table: "Positions");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_Symbol",
                table: "Positions",
                column: "Symbol",
                unique: true);
        }
    }
}
