using System;
using Microsoft.EntityFrameworkCore.Migrations;
using VeilGate.Infrastructure.Persistence.Sql;

#nullable disable

namespace VeilGate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "llm_providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    base_url = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    api_key_enc = table.Column<byte[]>(type: "bytea", nullable: false),
                    dek_version = table.Column<int>(type: "integer", nullable: false),
                    key_last4 = table.Column<string>(type: "char(4)", nullable: false),
                    models = table.Column<string[]>(type: "text[]", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_providers", x => x.id);
                    table.CheckConstraint("ck_llm_providers_dek", "dek_version >= 1");
                    table.CheckConstraint("ck_llm_providers_models", "cardinality(models) > 0");
                    table.CheckConstraint("ck_llm_providers_timeout", "timeout_seconds between 1 and 300");
                    table.ForeignKey(
                        name: "FK_llm_providers_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_llm_providers_tenant_id_name",
                table: "llm_providers",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_llm_providers_tenant_priority",
                table: "llm_providers",
                columns: new[] { "tenant_id", "priority" });

            migrationBuilder.ProtectTenantTable("llm_providers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "llm_providers");
        }
    }
}
