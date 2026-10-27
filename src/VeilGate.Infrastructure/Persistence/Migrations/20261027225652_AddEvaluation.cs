using System;
using Microsoft.EntityFrameworkCore.Migrations;
using VeilGate.Infrastructure.Persistence.Sql;

#nullable disable

namespace VeilGate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEvaluation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "eval_datasets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    case_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eval_datasets", x => x.id);
                    table.CheckConstraint("ck_eval_datasets_cases", "case_count >= 1");
                    table.ForeignKey(
                        name: "FK_eval_datasets_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "eval_cases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    expected = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eval_cases", x => x.id);
                    table.CheckConstraint("ck_eval_cases_ordinal", "ordinal >= 0");
                    table.ForeignKey(
                        name: "FK_eval_cases_eval_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "eval_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_eval_cases_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "eval_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    recall_threshold = table.Column<double>(type: "double precision", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    total_cases = table.Column<int>(type: "integer", nullable: false),
                    done_cases = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    recall = table.Column<double>(type: "double precision", nullable: true),
                    precision = table.Column<double>(type: "double precision", nullable: true),
                    f1 = table.Column<double>(type: "double precision", nullable: true),
                    leak_rate = table.Column<double>(type: "double precision", nullable: true),
                    leaks = table.Column<int>(type: "integer", nullable: true),
                    gate_passed = table.Column<bool>(type: "boolean", nullable: true),
                    per_type = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eval_runs", x => x.id);
                    table.CheckConstraint("ck_eval_runs_mode", "mode in ('rules','rules+ner')");
                    table.CheckConstraint("ck_eval_runs_progress", "total_cases >= 1 and done_cases between 0 and total_cases");
                    table.CheckConstraint("ck_eval_runs_state", "state in ('Queued','Running','Succeeded','Failed')");
                    table.CheckConstraint("ck_eval_runs_threshold", "recall_threshold between 0 and 1");
                    table.ForeignKey(
                        name: "FK_eval_runs_eval_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "eval_datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_eval_runs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "eval_run_leaks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    start_pos = table.Column<int>(type: "integer", nullable: false),
                    end_pos = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eval_run_leaks", x => x.id);
                    table.ForeignKey(
                        name: "FK_eval_run_leaks_eval_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "eval_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_eval_run_leaks_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_eval_cases_dataset_id_ordinal",
                table: "eval_cases",
                columns: new[] { "dataset_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_eval_cases_tenant_id",
                table: "eval_cases",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_eval_datasets_tenant_id_name",
                table: "eval_datasets",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_eval_run_leaks_run",
                table: "eval_run_leaks",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_eval_run_leaks_tenant_id",
                table: "eval_run_leaks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_eval_runs_dataset_id",
                table: "eval_runs",
                column: "dataset_id");

            migrationBuilder.CreateIndex(
                name: "ix_eval_runs_queue",
                table: "eval_runs",
                column: "created_at",
                filter: "state = 'Queued'");

            migrationBuilder.CreateIndex(
                name: "ix_eval_runs_tenant_created",
                table: "eval_runs",
                columns: new[] { "tenant_id", "created_at" });
                    migrationBuilder.ProtectTenantTable("eval_datasets");
            migrationBuilder.ProtectTenantTable("eval_cases");
            migrationBuilder.ProtectTenantTable("eval_runs");
            migrationBuilder.ProtectTenantTable("eval_run_leaks");
            migrationBuilder.CreateEvalClaimFunction();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "eval_cases");

            migrationBuilder.DropTable(
                name: "eval_run_leaks");

            migrationBuilder.DropTable(
                name: "eval_runs");

            migrationBuilder.DropTable(
                name: "eval_datasets");
                        migrationBuilder.Sql("DROP FUNCTION IF EXISTS eval_claim_next_run(int, int);");
        }
    }
}
