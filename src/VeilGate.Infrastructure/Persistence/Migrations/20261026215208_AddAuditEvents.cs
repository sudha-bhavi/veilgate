using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using VeilGate.Infrastructure.Persistence.Sql;

#nullable disable

namespace VeilGate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    result = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false),
                    prev_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                    table.CheckConstraint("ck_audit_events_actor", "actor_type in ('user','client','system')");
                    table.CheckConstraint("ck_audit_events_hash", "length(hash) = 64 and length(prev_hash) = 64");
                    table.CheckConstraint("ck_audit_events_result", "result in ('success','failure','denied')");
                    table.CheckConstraint("ck_audit_events_seq", "seq >= 1");
                    table.ForeignKey(
                        name: "FK_audit_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_action",
                table: "audit_events",
                columns: new[] { "tenant_id", "action", "occurred_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor",
                table: "audit_events",
                columns: new[] { "tenant_id", "actor_id", "occurred_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_time",
                table: "audit_events",
                columns: new[] { "tenant_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ux_audit_events_tenant_seq",
                table: "audit_events",
                columns: new[] { "tenant_id", "seq" },
                unique: true);
                   migrationBuilder.ProtectAppendOnlyTable("audit_events");
            migrationBuilder.CreateAuditAppendOnlyTriggers();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");
        }
    }
}
