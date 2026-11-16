using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SelectLunch.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdvanceVotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdvanceVotes",
                columns: table => new
                {
                    ChannelId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    SlackUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    RestaurantId = table.Column<long>(type: "INTEGER", nullable: true),
                    VotedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvanceVotes", x => new { x.ChannelId, x.Date, x.SlackUserId });
                    table.ForeignKey(
                        name: "FK_AdvanceVotes_Restaurants_RestaurantId",
                        column: x => x.RestaurantId,
                        principalTable: "Restaurants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdvanceVotes_RestaurantId",
                table: "AdvanceVotes",
                column: "RestaurantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdvanceVotes");
        }
    }
}
