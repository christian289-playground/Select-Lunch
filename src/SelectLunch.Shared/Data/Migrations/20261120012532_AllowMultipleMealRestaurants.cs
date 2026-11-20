using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SelectLunch.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowMultipleMealRestaurants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MealRecords_ChannelId_Date",
                table: "MealRecords");

            migrationBuilder.CreateIndex(
                name: "IX_MealRecords_ChannelId_Date_RestaurantId",
                table: "MealRecords",
                columns: new[] { "ChannelId", "Date", "RestaurantId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MealRecords_ChannelId_Date_RestaurantId",
                table: "MealRecords");

            migrationBuilder.CreateIndex(
                name: "IX_MealRecords_ChannelId_Date",
                table: "MealRecords",
                columns: new[] { "ChannelId", "Date" },
                unique: true);
        }
    }
}
