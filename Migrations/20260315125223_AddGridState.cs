using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClaudeTradingBot.Migrations
{
    /// <inheritdoc />
    public partial class AddGridState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GridStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    CenterPrice = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    GridSpacingPips = table.Column<double>(type: "REAL", nullable: false),
                    LotSizePerLevel = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LevelsJson = table.Column<string>(type: "TEXT", nullable: false),
                    TotalPnL = table.Column<decimal>(type: "decimal(18,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GridStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GridStates_AccountId_Symbol",
                table: "GridStates",
                columns: new[] { "AccountId", "Symbol" });

            migrationBuilder.CreateIndex(
                name: "IX_GridStates_Status",
                table: "GridStates",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GridStates");
        }
    }
}
