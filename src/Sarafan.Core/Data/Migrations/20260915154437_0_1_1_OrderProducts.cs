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
    public partial class _0_1_1_OrderProducts : Migration
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

            migrationBuilder.AddColumn<long>(
                name: "created_limit_eur_rate_id",
                table: "orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "created_limit_usd_rate_id",
                table: "orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "override_color",
                table: "orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "override_comment",
                table: "orders",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "override_product_name",
                table: "orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "override_quantity",
                table: "orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "override_seller_price",
                table: "orders",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "override_seller_price_currency",
                table: "orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "override_size",
                table: "orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "submitted_color",
                table: "orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "submitted_product_name",
                table: "orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "submitted_seller_price",
                table: "orders",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "submitted_seller_price_currency",
                table: "orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "submitted_size",
                table: "orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "updated_limit_eur_rate_id",
                table: "orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "updated_limit_usd_rate_id",
                table: "orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "order_product_audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    order_id = table.Column<long>(type: "bigint", nullable: false),
                    actor_id = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    before = table.Column<string>(type: "jsonb", nullable: false),
                    after = table.Column<string>(type: "jsonb", nullable: false),
                    usd_rate_id = table.Column<long>(type: "bigint", nullable: false),
                    eur_rate_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_product_audit_events", x => x.id);
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

            migrationBuilder.CreateIndex(
                name: "IX_orders_created_limit_eur_rate_id",
                table: "orders",
                column: "created_limit_eur_rate_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_created_limit_usd_rate_id",
                table: "orders",
                column: "created_limit_usd_rate_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_updated_limit_eur_rate_id",
                table: "orders",
                column: "updated_limit_eur_rate_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_updated_limit_usd_rate_id",
                table: "orders",
                column: "updated_limit_usd_rate_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_created_limit_pair",
                table: "orders",
                sql: "(created_limit_usd_rate_id IS NULL) = (created_limit_eur_rate_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_override_product",
                table: "orders",
                sql: "(override_quantity IS NULL AND override_product_name IS NULL AND override_seller_price IS NULL AND override_seller_price_currency IS NULL AND override_color IS NULL AND override_size IS NULL AND override_comment IS NULL) OR (override_quantity IS NOT NULL AND override_quantity BETWEEN 1 AND 4 AND override_product_name IS NOT NULL AND char_length(override_product_name) > 0 AND override_seller_price IS NOT NULL AND override_seller_price > 0 AND override_seller_price_currency IS NOT NULL AND override_seller_price_currency = 840)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_seller_price",
                table: "orders",
                sql: "(seller_price IS NULL AND seller_price_currency IS NULL) OR (seller_price > 0 AND seller_price_currency IN (643, 840, 978))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_submitted_product",
                table: "orders",
                sql: "(submitted_product_name IS NULL AND submitted_seller_price IS NULL AND submitted_seller_price_currency IS NULL AND submitted_color IS NULL AND submitted_size IS NULL) OR (submitted_product_name IS NOT NULL AND char_length(submitted_product_name) > 0 AND submitted_seller_price IS NOT NULL AND submitted_seller_price > 0 AND submitted_seller_price_currency IS NOT NULL AND submitted_seller_price_currency = 840 AND quantity BETWEEN 1 AND 4)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_updated_limit_pair",
                table: "orders",
                sql: "(updated_limit_usd_rate_id IS NULL) = (updated_limit_eur_rate_id IS NULL)");

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
                name: "IX_order_product_audit_events_order_id_occurred_at",
                table: "order_product_audit_events",
                columns: new[] { "order_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_order_product_audit_events_usd_rate_id",
                table: "order_product_audit_events",
                column: "usd_rate_id");

            migrationBuilder.AddForeignKey(
                name: "FK_orders_exchange_rate_history_created_limit_eur_rate_id",
                table: "orders",
                column: "created_limit_eur_rate_id",
                principalTable: "exchange_rate_history",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_orders_exchange_rate_history_created_limit_usd_rate_id",
                table: "orders",
                column: "created_limit_usd_rate_id",
                principalTable: "exchange_rate_history",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_orders_exchange_rate_history_updated_limit_eur_rate_id",
                table: "orders",
                column: "updated_limit_eur_rate_id",
                principalTable: "exchange_rate_history",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_orders_exchange_rate_history_updated_limit_usd_rate_id",
                table: "orders",
                column: "updated_limit_usd_rate_id",
                principalTable: "exchange_rate_history",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_orders_exchange_rate_history_created_limit_eur_rate_id",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "FK_orders_exchange_rate_history_created_limit_usd_rate_id",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "FK_orders_exchange_rate_history_updated_limit_eur_rate_id",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "FK_orders_exchange_rate_history_updated_limit_usd_rate_id",
                table: "orders");

            migrationBuilder.DropTable(
                name: "order_product_audit_events");

            migrationBuilder.DropIndex(
                name: "IX_orders_created_limit_eur_rate_id",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_orders_created_limit_usd_rate_id",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_orders_updated_limit_eur_rate_id",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_orders_updated_limit_usd_rate_id",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_created_limit_pair",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_override_product",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_seller_price",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_submitted_product",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_updated_limit_pair",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_exchange_rate_base_currency",
                table: "exchange_rate_history");

            migrationBuilder.DropCheckConstraint(
                name: "CK_exchange_rate_quote_currency",
                table: "exchange_rate_history");

            migrationBuilder.DropColumn(
                name: "created_limit_eur_rate_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "created_limit_usd_rate_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "override_color",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "override_comment",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "override_product_name",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "override_quantity",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "override_seller_price",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "override_seller_price_currency",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "override_size",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "submitted_color",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "submitted_product_name",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "submitted_seller_price",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "submitted_seller_price_currency",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "submitted_size",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "updated_limit_eur_rate_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "updated_limit_usd_rate_id",
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
