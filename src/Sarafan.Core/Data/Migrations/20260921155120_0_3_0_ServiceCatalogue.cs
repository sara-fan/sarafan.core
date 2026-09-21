using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Sarafan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class _0_3_0_ServiceCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_catalogue_audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    entry_id = table.Column<long>(type: "bigint", nullable: false),
                    service = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<int>(type: "integer", nullable: false),
                    actor_id = table.Column<int>(type: "integer", nullable: false),
                    actor_name = table.Column<string>(type: "character varying(605)", maxLength: 605, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    before = table.Column<string>(type: "jsonb", nullable: true),
                    after = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_catalogue_audit_events", x => x.id);
                    table.CheckConstraint("ck_service_catalogue_audit_action", "action IN (0, 100, 200)");
                    table.CheckConstraint("ck_service_catalogue_audit_actor_name", "btrim(actor_name) <> ''");
                    table.CheckConstraint("ck_service_catalogue_audit_service", "service IN (0, 100, 200, 300, 400, 500, 600, 700)");
                    table.CheckConstraint("ck_service_catalogue_audit_snapshots", "(action = 0 AND before IS NULL AND after IS NOT NULL)\nOR (action = 100 AND before IS NOT NULL AND after IS NOT NULL)\nOR (action = 200 AND before IS NOT NULL AND after IS NULL)");
                    table.ForeignKey(
                        name: "FK_service_catalogue_audit_events_backoffice_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "backoffice_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_catalogue_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    service = table.Column<int>(type: "integer", nullable: false),
                    price_method = table.Column<int>(type: "integer", nullable: false),
                    percentage = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    minimum_amount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    maximum_amount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    currency = table.Column<int>(type: "integer", nullable: true),
                    available_from = table.Column<DateOnly>(type: "date", nullable: false),
                    available_by = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_catalogue_entries", x => x.id);
                    table.CheckConstraint("ck_service_catalogue_currency", "currency IS NULL OR currency IN (643, 840)");
                    table.CheckConstraint("ck_service_catalogue_method", "price_method IN (0, 100, 200)");
                    table.CheckConstraint("ck_service_catalogue_parameters", "(price_method = 0 AND percentage IS NOT NULL AND percentage > 0 AND percentage <= 100\n    AND amount IS NULL AND currency IS NULL\n    AND (minimum_amount IS NULL OR minimum_amount >= 0)\n    AND (maximum_amount IS NULL OR maximum_amount >= 0)\n    AND (minimum_amount IS NULL OR maximum_amount IS NULL OR maximum_amount >= minimum_amount))\nOR (price_method = 100 AND percentage IS NULL AND minimum_amount IS NULL AND maximum_amount IS NULL\n    AND amount IS NOT NULL AND amount >= 0 AND currency IN (643, 840))\nOR (price_method = 200 AND percentage IS NULL AND minimum_amount IS NULL AND maximum_amount IS NULL\n    AND amount IS NULL AND currency IN (643, 840))");
                    table.CheckConstraint("ck_service_catalogue_period", "available_by IS NULL OR available_by >= available_from");
                    table.CheckConstraint("ck_service_catalogue_service", "service IN (0, 100, 200, 300, 400, 500, 600, 700)");
                    table.CheckConstraint("ck_service_catalogue_timestamps", "updated_at >= created_at");
                    table.CheckConstraint("ck_service_catalogue_version", "version <> '00000000-0000-0000-0000-000000000000'::uuid");
                });

            migrationBuilder.CreateIndex(
                name: "ix_service_catalogue_audit_at_id",
                table: "service_catalogue_audit_events",
                columns: new[] { "at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_service_catalogue_audit_entry_at_id",
                table: "service_catalogue_audit_events",
                columns: new[] { "entry_id", "at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_service_catalogue_audit_events_actor_id",
                table: "service_catalogue_audit_events",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_catalogue_audit_service_at_id",
                table: "service_catalogue_audit_events",
                columns: new[] { "service", "at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_service_catalogue_service_available_from_id",
                table: "service_catalogue_entries",
                columns: new[] { "service", "available_from", "id" });

            migrationBuilder.Sql("""
                ALTER TABLE service_catalogue_entries
                ADD CONSTRAINT ex_service_catalogue_entries_service_period
                EXCLUDE USING gist (
                    int4range(service, service, '[]') WITH &&,
                    daterange(available_from, available_by, '[]') WITH &&
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_catalogue_audit_events");

            migrationBuilder.DropTable(
                name: "service_catalogue_entries");
        }
    }
}
