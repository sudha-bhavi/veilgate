using System;
using Microsoft.EntityFrameworkCore.Migrations;
using VeilGate.Infrastructure.Persistence.Sql;

#nullable disable

namespace VeilGate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMetricsTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gateway_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    request_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    provider = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    policy_version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    entity_count = table.Column<int>(type: "integer", nullable: false),
                    entities = table.Column<string>(type: "jsonb", nullable: false),
                    latency_ms = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_requests", x => x.id);
                    table.CheckConstraint("ck_gateway_requests_actor", "actor_type in ('user','client')");
                    table.CheckConstraint("ck_gateway_requests_entities", "entity_count >= 0");
                    table.CheckConstraint("ck_gateway_requests_status", "status in ('success','failure','denied')");
                    table.ForeignKey(
                        name: "FK_gateway_requests_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "metrics_hourly",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hour = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requests = table.Column<int>(type: "integer", nullable: false),
                    blocked = table.Column<int>(type: "integer", nullable: false),
                    failed = table.Column<int>(type: "integer", nullable: false),
                    entities = table.Column<long>(type: "bigint", nullable: false),
                    entities_by_type = table.Column<string>(type: "jsonb", nullable: false),
                    latency_buckets = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metrics_hourly", x => new { x.tenant_id, x.hour, x.client_id });
                    table.CheckConstraint("ck_metrics_hourly_counts", "requests >= 0 and blocked >= 0 and failed >= 0 and entities >= 0");
                    table.ForeignKey(
                        name: "FK_metrics_hourly_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "processed_messages",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_messages", x => new { x.consumer, x.message_id });
                    table.ForeignKey(
                        name: "FK_processed_messages_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_gateway_requests_client",
                table: "gateway_requests",
                columns: new[] { "tenant_id", "client_id", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_gateway_requests_problems",
                table: "gateway_requests",
                columns: new[] { "tenant_id", "status", "created_at" },
                descending: new[] { false, false, true },
                filter: "status <> 'success'");

            migrationBuilder.CreateIndex(
                name: "ix_gateway_requests_time",
                table: "gateway_requests",
                columns: new[] { "tenant_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_processed_messages_age",
                table: "processed_messages",
                column: "processed_at");

            migrationBuilder.CreateIndex(
                name: "IX_processed_messages_tenant_id",
                table: "processed_messages",
                column: "tenant_id");

            // The consumer sets the tenant of each message before it writes, so row-level security applies as for any other table.
            migrationBuilder.ProtectTenantTable("gateway_requests");
            migrationBuilder.ProtectTenantTable("metrics_hourly");
            migrationBuilder.ProtectTenantTable("processed_messages");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gateway_requests");

            migrationBuilder.DropTable(
                name: "metrics_hourly");

            migrationBuilder.DropTable(
                name: "processed_messages");
        }
    }
}
