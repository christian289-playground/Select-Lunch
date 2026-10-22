using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SelectLunch.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRestaurantDepartureTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeOnly>(
                name: "DepartureTime",
                table: "Restaurants",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DepartureTime",
                table: "Restaurants");
        }
    }
}
