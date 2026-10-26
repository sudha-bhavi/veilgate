using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using VeilGate.Infrastructure.Persistence.Sql;

#nullable disable

namespace VeilGate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_keys",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dek_version = table.Column<int>(type: "integer", nullable: false),
                    wrapped_dek = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_keys", x => new { x.tenant_id, x.dek_version });
                    table.CheckConstraint("ck_tenant_keys_status", "status in ('Active','Retired')");
                    table.ForeignKey(
                        name: "FK_tenant_keys_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vault_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vault_sessions", x => x.id);
                    table.UniqueConstraint("AK_vault_sessions_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_vault_sessions_ttl", "expires_at <= created_at + interval '24 hours'");
                    table.ForeignKey(
                        name: "FK_vault_sessions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vault_tokens",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    dek_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vault_tokens", x => x.id);
                    table.CheckConstraint("ck_vault_tokens_nonce", "length(nonce) = 12");
                    table.CheckConstraint("ck_vault_tokens_tag", "length(tag) = 16");
                    table.ForeignKey(
                        name: "FK_vault_tokens_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vault_tokens_vault_sessions_tenant_id_session_id",
                        columns: x => new { x.tenant_id, x.session_id },
                        principalTable: "vault_sessions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_vault_sessions_expires",
                table: "vault_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_vault_tokens_tenant_id_session_id",
                table: "vault_tokens",
                columns: new[] { "tenant_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ux_vault_tokens_nonce",
                table: "vault_tokens",
                columns: new[] { "tenant_id", "dek_version", "nonce" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_vault_tokens_session_token",
                table: "vault_tokens",
                columns: new[] { "session_id", "token" },
                unique: true);

            migrationBuilder.ProtectTenantTable("tenant_keys");
            migrationBuilder.ProtectTenantTable("vault_sessions");
            migrationBuilder.ProtectTenantTable("vault_tokens");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_keys");

            migrationBuilder.DropTable(
                name: "vault_tokens");

            migrationBuilder.DropTable(
                name: "vault_sessions");
        }
    }
}
