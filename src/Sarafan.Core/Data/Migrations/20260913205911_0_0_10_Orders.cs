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
            migrationBuilder.Sql(
                """
                ALTER TABLE exchange_rate_history
                    ALTER COLUMN base_currency TYPE integer
                        USING (CASE base_currency WHEN 'RUB' THEN 643 WHEN 'USD' THEN 840 ELSE NULL END),
                    ALTER COLUMN quote_currency TYPE integer
                        USING (CASE quote_currency WHEN 'RUB' THEN 643 WHEN 'USD' THEN 840 ELSE NULL END);
                """);

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
                    product_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    store_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    image_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    seller_price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    seller_price_currency = table.Column<int>(type: "integer", nullable: true),
                    length_cm = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    width_cm = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    height_cm = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    characteristics = table.Column<string>(type: "jsonb", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    applied_exchange_rate_history_id = table.Column<long>(type: "bigint", nullable: true),
                    creation_idempotency_key = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                    table.CheckConstraint("ck_orders_applied_exchange_rate", "applied_exchange_rate_history_id IS NULL OR seller_price_currency IS NOT NULL");
                    table.CheckConstraint("ck_orders_creation_idempotency_key", "creation_idempotency_key <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_orders_customer_order_number", "customer_order_number > 0");
                    table.CheckConstraint("ck_orders_dimensions", "(length_cm IS NULL AND width_cm IS NULL AND height_cm IS NULL) OR (length_cm > 0 AND width_cm > 0 AND height_cm > 0)");
                    table.CheckConstraint("ck_orders_image_url", "image_url IS NULL OR image_url ~* '^https?://' AND char_length(image_url) <= 2048");
                    table.CheckConstraint("ck_orders_quantity", "quantity > 0");
                    table.CheckConstraint("ck_orders_seller_price", "(seller_price IS NULL AND seller_price_currency IS NULL) OR (seller_price > 0 AND seller_price_currency IN (643, 840))");
                    table.CheckConstraint("ck_orders_source_url", "source_url ~* '^https?://' AND char_length(source_url) <= 2048");
                    table.CheckConstraint("ck_orders_status", "status IN (0, 100, 200, 300, 310, 320, 330, 340, 360, 380, 400, 500)");
                    table.ForeignKey(
                        name: "FK_orders_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_orders_exchange_rate_history_applied_exchange_rate_history_~",
                        column: x => x.applied_exchange_rate_history_id,
                        principalTable: "exchange_rate_history",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_exchange_rate_base_currency",
                table: "exchange_rate_history",
                sql: "base_currency IN (643, 840)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_exchange_rate_distinct_currencies",
                table: "exchange_rate_history",
                sql: "base_currency <> quote_currency");

            migrationBuilder.AddCheckConstraint(
                name: "CK_exchange_rate_quote_currency",
                table: "exchange_rate_history",
                sql: "quote_currency IN (643, 840)");

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
                name: "ix_orders_applied_exchange_rate_history_id",
                table: "orders",
                column: "applied_exchange_rate_history_id");

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

            migrationBuilder.DropCheckConstraint(
                name: "CK_exchange_rate_base_currency",
                table: "exchange_rate_history");

            migrationBuilder.DropCheckConstraint(
                name: "CK_exchange_rate_distinct_currencies",
                table: "exchange_rate_history");

            migrationBuilder.DropCheckConstraint(
                name: "CK_exchange_rate_quote_currency",
                table: "exchange_rate_history");

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

            migrationBuilder.Sql(
                """
                ALTER TABLE exchange_rate_history
                    ALTER COLUMN base_currency TYPE character varying(3)
                        USING (CASE base_currency WHEN 643 THEN 'RUB' WHEN 840 THEN 'USD' ELSE NULL END),
                    ALTER COLUMN quote_currency TYPE character varying(3)
                        USING (CASE quote_currency WHEN 643 THEN 'RUB' WHEN 840 THEN 'USD' ELSE NULL END);
                """);
        }
    }
}
