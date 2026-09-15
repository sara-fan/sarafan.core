// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Sarafan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class _0_1_1_Orders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_seller_price",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_exchange_rate_base_currency",
                table: "exchange_rate_history");

            migrationBuilder.DropCheckConstraint(
                name: "CK_exchange_rate_quote_currency",
                table: "exchange_rate_history");

            migrationBuilder.AddColumn<string>(
                name: "color",
                table: "orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "size",
                table: "orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "order_product_audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    order_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    actor_id = table.Column<int>(type: "integer", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    before = table.Column<string>(type: "jsonb", nullable: true),
                    after = table.Column<string>(type: "jsonb", nullable: false),
                    usd_rate_id = table.Column<long>(type: "bigint", nullable: true),
                    eur_rate_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_product_audit_events", x => x.id);
                    table.CheckConstraint("ck_order_product_audit_kind", "kind IN (0, 100, 200)");
                    table.CheckConstraint("ck_order_product_audit_rate_pair", "(usd_rate_id IS NULL) = (eur_rate_id IS NULL)");
                    table.CheckConstraint("ck_order_product_audit_source", "(kind = 200 AND actor_id IS NOT NULL AND before IS NOT NULL AND usd_rate_id IS NOT NULL) OR (kind IN (0, 100) AND actor_id IS NULL)");
                    table.ForeignKey(
                        name: "FK_order_product_audit_events_backoffice_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "backoffice_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_product_audit_events_exchange_rate_history_eur_rate_id",
                        column: x => x.eur_rate_id,
                        principalTable: "exchange_rate_history",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_product_audit_events_exchange_rate_history_usd_rate_id",
                        column: x => x.usd_rate_id,
                        principalTable: "exchange_rate_history",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_product_audit_events_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_seller_price",
                table: "orders",
                sql: "(seller_price IS NULL AND seller_price_currency IS NULL) OR (seller_price > 0 AND seller_price_currency IN (643, 840, 978))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_exchange_rate_base_currency",
                table: "exchange_rate_history",
                sql: "base_currency IN (643, 840, 978)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_exchange_rate_quote_currency",
                table: "exchange_rate_history",
                sql: "quote_currency IN (643, 840, 978)");

            migrationBuilder.CreateIndex(
                name: "IX_order_product_audit_events_actor_id",
                table: "order_product_audit_events",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_product_audit_events_eur_rate_id",
                table: "order_product_audit_events",
                column: "eur_rate_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_product_audit_events_order_id_kind",
                table: "order_product_audit_events",
                columns: new[] { "order_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "IX_order_product_audit_events_order_id_occurred_at",
                table: "order_product_audit_events",
                columns: new[] { "order_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_order_product_audit_events_usd_rate_id",
                table: "order_product_audit_events",
                column: "usd_rate_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_product_audit_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_seller_price",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_exchange_rate_base_currency",
                table: "exchange_rate_history");

            migrationBuilder.DropCheckConstraint(
                name: "CK_exchange_rate_quote_currency",
                table: "exchange_rate_history");

            migrationBuilder.DropColumn(
                name: "color",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "size",
                table: "orders");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_seller_price",
                table: "orders",
                sql: "(seller_price IS NULL AND seller_price_currency IS NULL) OR (seller_price > 0 AND seller_price_currency IN (643, 840))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_exchange_rate_base_currency",
                table: "exchange_rate_history",
                sql: "base_currency IN (643, 840)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_exchange_rate_quote_currency",
                table: "exchange_rate_history",
                sql: "quote_currency IN (643, 840)");
        }
    }
}
