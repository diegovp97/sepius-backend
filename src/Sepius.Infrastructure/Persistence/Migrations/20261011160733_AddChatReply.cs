using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sepius.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatReply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReplyToId",
                table: "ChatMessages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplyToNickname",
                table: "ChatMessages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplyToText",
                table: "ChatMessages",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplyToId",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "ReplyToNickname",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "ReplyToText",
                table: "ChatMessages");
        }
    }
}
