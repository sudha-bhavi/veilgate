using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VeilGate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionIpUserAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<IPAddress>(
                name: "ip",
                table: "sessions",
                type: "inet",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "user_agent",
                table: "sessions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ip",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "user_agent",
                table: "sessions");
        }
    }
}
