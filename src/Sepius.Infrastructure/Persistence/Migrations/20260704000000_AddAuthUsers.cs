using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sepius.Infrastructure.Persistence.Migrations;

public partial class AddAuthUsers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AuthUsers",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Username = table.Column<string>(type: "text", nullable: false),
                PasswordHash = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuthUsers", x => x.Id);
            });

        var defaultHash = HashSha256("sepius2026");
        migrationBuilder.InsertData(
            table: "AuthUsers",
            columns: ["Username", "PasswordHash", "CreatedAt"],
            values: ["admin", defaultHash, DateTime.UtcNow]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AuthUsers");
    }

    private static string HashSha256(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(bytes);
    }
}
