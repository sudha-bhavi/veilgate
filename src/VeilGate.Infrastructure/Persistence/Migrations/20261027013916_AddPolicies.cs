using System;
using Microsoft.EntityFrameworkCore.Migrations;
using VeilGate.Infrastructure.Persistence.Sql;

#nullable disable

namespace VeilGate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    draft_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_version_no = table.Column<int>(type: "integer", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policies", x => x.id);
                    table.UniqueConstraint("AK_policies_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "FK_policies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policy_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    definition = table.Column<string>(type: "text", nullable: false),
                    definition_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    submitted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    change_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reject_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policy_versions", x => x.id);
                    table.UniqueConstraint("AK_policy_versions_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_policy_versions_approver", "approved_by is null or approved_by <> submitted_by");
                    table.CheckConstraint("ck_policy_versions_state", "state in ('Draft','PendingApproval','Published','Rejected','Archived')");
                    table.ForeignKey(
                        name: "FK_policy_versions_policies_tenant_id_policy_id",
                        columns: x => new { x.tenant_id, x.policy_id },
                        principalTable: "policies",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_policy_versions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_policy_versions_users_approved_by",
                        column: x => x.approved_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_policy_versions_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_policy_versions_users_submitted_by",
                        column: x => x.submitted_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_policies_tenant_id_name",
                table: "policies",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_policy_versions_approved_by",
                table: "policy_versions",
                column: "approved_by");

            migrationBuilder.CreateIndex(
                name: "IX_policy_versions_created_by",
                table: "policy_versions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_policy_versions_policy_id_version_no",
                table: "policy_versions",
                columns: new[] { "policy_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_policy_versions_submitted_by",
                table: "policy_versions",
                column: "submitted_by");

            migrationBuilder.CreateIndex(
                name: "IX_policy_versions_tenant_id_policy_id",
                table: "policy_versions",
                columns: new[] { "tenant_id", "policy_id" });

            migrationBuilder.CreateIndex(
                name: "ix_policy_versions_tenant_state",
                table: "policy_versions",
                columns: new[] { "tenant_id", "state" });
                   migrationBuilder.ProtectTenantTable("policies");
            migrationBuilder.ProtectTenantTable("policy_versions");

            // policies <-> policy_versions point at each other, so these are checked at commit time, not per statement.
            migrationBuilder.Sql("""
                ALTER TABLE policies ADD CONSTRAINT fk_policies_current_version
                    FOREIGN KEY (tenant_id, current_version_id) REFERENCES policy_versions (tenant_id, id) DEFERRABLE INITIALLY DEFERRED;
                ALTER TABLE policies ADD CONSTRAINT fk_policies_draft_version
                    FOREIGN KEY (tenant_id, draft_version_id) REFERENCES policy_versions (tenant_id, id) DEFERRABLE INITIALLY DEFERRED;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "policy_versions");

            migrationBuilder.DropTable(
                name: "policies");
                        migrationBuilder.Sql("""
                ALTER TABLE policies DROP CONSTRAINT IF EXISTS fk_policies_current_version;
                ALTER TABLE policies DROP CONSTRAINT IF EXISTS fk_policies_draft_version;
                """);
        }
    }
}
