using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SelectLunch.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChannelMembers",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ChannelId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    SlackUserId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    RealName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelMembers", x => new { x.WorkspaceId, x.ChannelId, x.SlackUserId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelMembers");
        }
    }
}
