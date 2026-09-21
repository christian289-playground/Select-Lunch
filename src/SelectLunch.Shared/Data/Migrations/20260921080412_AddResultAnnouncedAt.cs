using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SelectLunch.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddResultAnnouncedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResultAnnouncedAt",
                table: "Polls",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResultAnnouncedAt",
                table: "Polls");
        }
    }
}
