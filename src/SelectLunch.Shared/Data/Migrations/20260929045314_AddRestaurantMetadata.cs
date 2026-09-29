using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SelectLunch.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRestaurantMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "Restaurants",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByDisplayName",
                table: "Restaurants",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MenuSourceUrl",
                table: "Restaurants",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "TodayMenuDate",
                table: "Restaurants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TodayMenuImageUrl",
                table: "Restaurants",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WaitLevel",
                table: "Restaurants",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "Categories",
                type: "TEXT",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 1L,
                column: "NormalizedName",
                value: "한식");

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 2L,
                column: "NormalizedName",
                value: "중식");

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 3L,
                column: "NormalizedName",
                value: "일식");

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 4L,
                column: "NormalizedName",
                value: "양식");

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 5L,
                column: "NormalizedName",
                value: "분식");

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 6L,
                column: "NormalizedName",
                value: "아시안");

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 7L,
                column: "NormalizedName",
                value: "기타");

            // 시드 외에 사용자가 이미 만든 행이 있다면 빈 문자열로 남아 UNIQUE 인덱스를 깬다.
            // 공백만 제거한 이름으로 채운다(시드는 위 UpdateData가 정확한 값을 넣는다).
            migrationBuilder.Sql(
                "UPDATE Categories SET NormalizedName = lower(replace(Name, ' ', '')) WHERE NormalizedName = '';");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_NormalizedName",
                table: "Categories",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Categories_NormalizedName",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "Address",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "CreatedByDisplayName",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "MenuSourceUrl",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "TodayMenuDate",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "TodayMenuImageUrl",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "WaitLevel",
                table: "Restaurants");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "Categories");
        }
    }
}
