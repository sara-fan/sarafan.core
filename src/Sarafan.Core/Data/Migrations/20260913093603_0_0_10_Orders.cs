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
    public partial class _0_0_10_Orders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "next_order_number",
                table: "customers",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "order_code",
                table: "customers",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION enforce_customer_order_code_immutable()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF OLD.order_code IS NOT NULL
                       AND NEW.order_code IS DISTINCT FROM OLD.order_code THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23514',
                            CONSTRAINT = 'ck_customers_order_code_immutable',
                            MESSAGE = 'customer order_code cannot be changed after assignment';
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER trg_customers_order_code_immutable
                BEFORE UPDATE OF order_code ON customers
                FOR EACH ROW
                EXECUTE FUNCTION enforce_customer_order_code_immutable();
                """);

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    customer_id = table.Column<int>(type: "integer", nullable: false),
                    customer_order_number = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    source_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    creation_idempotency_key = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                    table.CheckConstraint("ck_orders_creation_idempotency_key", "creation_idempotency_key <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_orders_customer_order_number", "customer_order_number > 0");
                    table.CheckConstraint("ck_orders_source_url", "source_url ~* '^https?://' AND char_length(source_url) <= 2048");
                    table.CheckConstraint("ck_orders_status", "status IN (0, 100, 200, 300, 310, 320, 330, 340, 360, 380, 400, 500)");
                    table.ForeignKey(
                        name: "FK_orders_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_customers_order_code",
                table: "customers",
                column: "order_code",
                unique: true,
                filter: "order_code IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customers_next_order_number",
                table: "customers",
                sql: "next_order_number > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customers_order_code",
                table: "customers",
                sql: "order_code IS NULL OR order_code ~ '^[0-9]{8}$'");

            migrationBuilder.CreateIndex(
                name: "ux_orders_creation_idempotency",
                table: "orders",
                columns: new[] { "customer_id", "creation_idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_orders_customer_order_number",
                table: "orders",
                columns: new[] { "customer_id", "customer_order_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS trg_customers_order_code_immutable ON customers;
                DROP FUNCTION IF EXISTS enforce_customer_order_code_immutable();
                """);

            migrationBuilder.DropIndex(
                name: "ux_customers_order_code",
                table: "customers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customers_next_order_number",
                table: "customers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customers_order_code",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "next_order_number",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "order_code",
                table: "customers");
        }
    }
}
