using System;
using Microsoft.EntityFrameworkCore.Migrations;
using VeilGate.Infrastructure.Persistence.Sql;

#nullable disable

namespace VeilGate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRecoveryCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recovery_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recovery_codes", x => x.id);
                    table.CheckConstraint("ck_recovery_codes_hash", "length(code_hash) = 64");
                    table.ForeignKey(
                        name: "FK_recovery_codes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recovery_codes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recovery_codes_tenant_id",
                table: "recovery_codes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_recovery_codes_user_hash",
                table: "recovery_codes",
                columns: new[] { "user_id", "code_hash" },
                unique: true);

                        migrationBuilder.ProtectTenantTable("recovery_codes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recovery_codes");
        }
    }
}
