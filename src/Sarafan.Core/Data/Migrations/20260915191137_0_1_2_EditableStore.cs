// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sarafan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class _0_1_2_EditableStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_override_product",
                table: "orders");

            migrationBuilder.AddColumn<string>(
                name: "override_store_name",
                table: "orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_override_product",
                table: "orders",
                sql: "(override_quantity IS NULL AND override_store_name IS NULL AND override_product_name IS NULL AND override_seller_price IS NULL AND override_seller_price_currency IS NULL AND override_color IS NULL AND override_size IS NULL AND override_comment IS NULL) OR (override_quantity IS NOT NULL AND override_quantity BETWEEN 1 AND 4 AND override_product_name IS NOT NULL AND char_length(override_product_name) > 0 AND override_seller_price IS NOT NULL AND override_seller_price > 0 AND override_seller_price_currency IS NOT NULL AND override_seller_price_currency = 840)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_override_product",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "override_store_name",
                table: "orders");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_override_product",
                table: "orders",
                sql: "(override_quantity IS NULL AND override_product_name IS NULL AND override_seller_price IS NULL AND override_seller_price_currency IS NULL AND override_color IS NULL AND override_size IS NULL AND override_comment IS NULL) OR (override_quantity IS NOT NULL AND override_quantity BETWEEN 1 AND 4 AND override_product_name IS NOT NULL AND char_length(override_product_name) > 0 AND override_seller_price IS NOT NULL AND override_seller_price > 0 AND override_seller_price_currency IS NOT NULL AND override_seller_price_currency = 840)");
        }
    }
}
