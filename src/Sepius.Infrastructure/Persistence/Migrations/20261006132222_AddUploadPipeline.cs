using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sepius.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUploadPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "upload_pipeline",
                columns: table => new
                {
                    FilePath = table.Column<string>(type: "text", nullable: false),
                    ChannelName = table.Column<string>(type: "text", nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DriveStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DriveFileId = table.Column<string>(type: "text", nullable: true),
                    DriveError = table.Column<string>(type: "text", nullable: true),
                    DriveAttempts = table.Column<int>(type: "integer", nullable: false),
                    YouTubeStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    YouTubeVideoId = table.Column<string>(type: "text", nullable: true),
                    YouTubeError = table.Column<string>(type: "text", nullable: true),
                    YouTubeAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_upload_pipeline", x => x.FilePath);
                });

            migrationBuilder.CreateIndex(
                name: "IX_upload_pipeline_UpdatedAt",
                table: "upload_pipeline",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "upload_pipeline");
        }
    }
}
