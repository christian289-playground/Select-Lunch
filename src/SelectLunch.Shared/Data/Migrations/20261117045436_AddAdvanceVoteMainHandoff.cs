using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SelectLunch.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdvanceVoteMainHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFromAdvanceVote",
                table: "PollVotes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSupersededByMainVote",
                table: "AdvanceVotes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsFromAdvanceVote",
                table: "PollVotes");

            migrationBuilder.DropColumn(
                name: "IsSupersededByMainVote",
                table: "AdvanceVotes");
        }
    }
}
