using Microsoft.EntityFrameworkCore.Migrations;
using VeilGate.Infrastructure.Persistence.Sql;

#nullable disable

namespace VeilGate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHousekeepingFunctions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateHousekeepingFunctions();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropHousekeepingFunctions();
        }
    }
}
