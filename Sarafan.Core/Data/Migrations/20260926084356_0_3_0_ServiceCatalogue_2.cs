// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sarafan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class _0_3_0_ServiceCatalogue_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_history_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    order_id = table.Column<long>(type: "bigint", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    areas = table.Column<int>(type: "integer", nullable: false),
                    actor_type = table.Column<int>(type: "integer", nullable: false),
                    actor_id = table.Column<int>(type: "integer", nullable: true),
                    actor_name = table.Column<string>(type: "character varying(605)", maxLength: 605, nullable: false),
                    product_audit_id = table.Column<long>(type: "bigint", nullable: true),
                    pricing_snapshot_id = table.Column<long>(type: "bigint", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_history_events", x => x.id);
                    table.ForeignKey("FK_order_history_events_orders_order_id", x => x.order_id, "orders", "id", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey("FK_order_history_events_order_product_audit_events_product_audit~", x => x.product_audit_id, "order_product_audit_events", "id", onDelete: ReferentialAction.Restrict);
                    table.ForeignKey("FK_order_history_events_order_pricing_snapshots_pricing_snapshot~", x => x.pricing_snapshot_id, "order_pricing_snapshots", "id", onDelete: ReferentialAction.Restrict);
                });
            migrationBuilder.CreateIndex("IX_order_history_events_order_id_at_id", "order_history_events", new[] { "order_id", "at", "id" });
            migrationBuilder.CreateIndex("IX_order_history_events_product_audit_id", "order_history_events", "product_audit_id", unique: true);
            migrationBuilder.CreateIndex("IX_order_history_events_pricing_snapshot_id", "order_history_events", "pricing_snapshot_id", unique: true);

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_catalogue_service",
                table: "service_catalogue_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_catalogue_audit_service",
                table: "service_catalogue_audit_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "order_history_events");
            migrationBuilder.AddCheckConstraint(
                name: "ck_service_catalogue_service",
                table: "service_catalogue_entries",
                sql: "service IN (0, 100, 200, 300, 400, 500, 600, 700)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_catalogue_audit_service",
                table: "service_catalogue_audit_events",
                sql: "service IN (0, 100, 200, 300, 400, 500, 600, 700)");
        }
    }
}
