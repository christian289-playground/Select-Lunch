using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SelectLunch.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRestaurantEarlyDeparture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EarlyDepartureSeconds",
                table: "Restaurants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EarlyDepartureSeconds",
                table: "Restaurants");
        }
    }
}
