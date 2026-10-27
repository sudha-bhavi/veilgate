using System;
using Microsoft.EntityFrameworkCore.Migrations;
using VeilGate.Infrastructure.Persistence.Sql;

#nullable disable

namespace VeilGate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddApiClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_clients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    default_policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ip_allowlist = table.Column<string[]>(type: "text[]", nullable: false),
                    rate_limit_per_minute = table.Column<int>(type: "integer", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_clients", x => x.id);
                    table.UniqueConstraint("AK_api_clients_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_api_clients_rate", "rate_limit_per_minute between 1 and 10000");
                    table.CheckConstraint("ck_api_clients_scopes", "cardinality(scopes) > 0 and scopes <@ ARRAY['gateway:invoke','gateway:detect','gateway:deidentify','gateway:reidentify']::text[]");
                    table.CheckConstraint("ck_api_clients_status", "status in ('Active','Revoked')");
                    table.ForeignKey(
                        name: "FK_api_clients_policies_tenant_id_default_policy_id",
                        columns: x => new { x.tenant_id, x.default_policy_id },
                        principalTable: "policies",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_api_clients_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_api_clients_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "api_client_secrets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_pk = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_hash = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_client_secrets", x => x.id);
                    table.ForeignKey(
                        name: "FK_api_client_secrets_api_clients_tenant_id_client_pk",
                        columns: x => new { x.tenant_id, x.client_pk },
                        principalTable: "api_clients",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_api_client_secrets_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_client_secrets_client",
                table: "api_client_secrets",
                column: "client_pk");

            migrationBuilder.CreateIndex(
                name: "IX_api_client_secrets_tenant_id_client_pk",
                table: "api_client_secrets",
                columns: new[] { "tenant_id", "client_pk" });

            migrationBuilder.CreateIndex(
                name: "IX_api_clients_client_id",
                table: "api_clients",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_clients_owner_user_id",
                table: "api_clients",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_clients_tenant_id_default_policy_id",
                table: "api_clients",
                columns: new[] { "tenant_id", "default_policy_id" });

            migrationBuilder.CreateIndex(
                name: "IX_api_clients_tenant_id_name",
                table: "api_clients",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_clients_tenant_status",
                table: "api_clients",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.ProtectTenantTable("api_clients");
            migrationBuilder.ProtectTenantTable("api_client_secrets");
            migrationBuilder.CreateClientLookupFunction();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS auth_find_client_tenant(varchar);");

            migrationBuilder.DropTable(
                name: "api_client_secrets");

            migrationBuilder.DropTable(
                name: "api_clients");
        }
    }
}
