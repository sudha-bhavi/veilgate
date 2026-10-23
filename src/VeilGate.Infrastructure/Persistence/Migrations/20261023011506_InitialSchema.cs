using System;
using Microsoft.EntityFrameworkCore.Migrations;
using VeilGate.Infrastructure.Persistence.Sql;

#nullable disable

namespace VeilGate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "citext", maxLength: 120, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.id);
                    table.CheckConstraint("ck_tenants_status", "status in ('Active','Suspended')");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "citext", nullable: false),
                    display_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    password_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_attempts = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    token_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                    table.CheckConstraint("ck_users_failed_attempts", "failed_attempts >= 0");
                    table.CheckConstraint("ck_users_role", "role in ('PlatformAdmin','TenantAdmin','SecurityOfficer','Developer','Auditor')");
                    table.CheckConstraint("ck_users_status", "status in ('Invited','Active','Disabled','Locked')");
                    table.CheckConstraint("ck_users_token_version", "token_version >= 1");
                    table.ForeignKey(
                        name: "FK_users_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenants_name",
                table: "tenants",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_tenant_role",
                table: "users",
                columns: new[] { "tenant_id", "role" });

            migrationBuilder.CreateIndex(
                name: "ux_users_email",
                table: "users",
                column: "email",
                unique: true);

                migrationBuilder.CreateTenantFunction();
                migrationBuilder.ProtectTenantsTable();
                migrationBuilder.ProtectTenantTable("users");
                migrationBuilder.CreateLoginLookupFunction();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
