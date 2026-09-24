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
            migrationBuilder.Sql("""
                CREATE FUNCTION valid_service_price_bands(value jsonb) RETURNS boolean
                LANGUAGE plpgsql IMMUTABLE STRICT AS $$
                DECLARE band jsonb; previous_end numeric; lower_value numeric; upper_value numeric; charge numeric; index integer := 0; count integer;
                BEGIN
                    IF jsonb_typeof(value) <> 'array' THEN RETURN false; END IF;
                    count := jsonb_array_length(value);
                    IF count < 1 OR count > 100 THEN RETURN false; END IF;
                    FOR band IN SELECT jsonb_array_elements(value) LOOP
                        index := index + 1;
                        IF jsonb_typeof(band) <> 'object' OR NOT (band ?& ARRAY['From','By','Amount'])
                            OR jsonb_typeof(band->'From') NOT IN ('number','null') OR jsonb_typeof(band->'Amount') <> 'number'
                            OR jsonb_typeof(band->'By') NOT IN ('number','null') THEN RETURN false; END IF;
                        lower_value := (band->>'From')::numeric;
                        upper_value := (band->>'By')::numeric;
                        charge := (band->>'Amount')::numeric;
                        IF index = 1 THEN
                            IF lower_value IS NOT NULL THEN RETURN false; END IF;
                        ELSIF lower_value IS NULL OR lower_value <> previous_end OR lower_value < 0
                            OR lower_value > 99999999.99 OR lower_value <> round(lower_value,2) THEN RETURN false; END IF;
                        IF charge < 0 OR charge > 99999999.99 OR charge <> round(charge,2) THEN RETURN false; END IF;
                        IF index = count THEN
                            IF upper_value IS NOT NULL THEN RETURN false; END IF;
                        ELSE
                            IF upper_value IS NULL OR upper_value < 0 OR (lower_value IS NOT NULL AND upper_value <= lower_value)
                                OR upper_value > 99999999.99 OR upper_value <> round(upper_value,2) THEN RETURN false; END IF;
                        END IF;
                        previous_end := upper_value;
                    END LOOP;
                    RETURN true;
                EXCEPTION WHEN OTHERS THEN RETURN false;
                END $$;
                """);

            migrationBuilder.CreateTable(
                name: "order_pricing_snapshots",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    order_id = table.Column<long>(type: "bigint", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    actor_id = table.Column<int>(type: "integer", nullable: true),
                    actor_name = table.Column<string>(type: "character varying(605)", maxLength: 605, nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_pricing_snapshots", x => x.id);
                    table.CheckConstraint("ck_order_pricing_actor", "(actor_id IS NULL AND actor_name IS NULL) OR (actor_id IS NOT NULL AND actor_name IS NOT NULL AND btrim(actor_name) <> '')");
                    table.CheckConstraint("ck_order_pricing_validity", "valid_until IS NULL OR valid_until > at");
                    table.ForeignKey(
                        name: "FK_order_pricing_snapshots_backoffice_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "backoffice_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_pricing_snapshots_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

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
                    table.CheckConstraint("ck_service_catalogue_audit_snapshots", "(action = 0 AND before IS NULL AND after IS NOT NULL)\r\nOR (action = 100 AND before IS NOT NULL AND after IS NOT NULL)\r\nOR (action = 200 AND before IS NOT NULL AND after IS NULL)");
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
                    interval_currency = table.Column<int>(type: "integer", nullable: true),
                    bands = table.Column<string>(type: "jsonb", nullable: false),
                    available_from = table.Column<DateOnly>(type: "date", nullable: true),
                    available_by = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_catalogue_entries", x => x.id);
                    table.CheckConstraint("ck_service_catalogue_bands", "(price_method = 400 AND interval_currency IS NOT NULL AND interval_currency IN (643, 840) AND valid_service_price_bands(bands)) OR (price_method <> 400 AND interval_currency IS NULL AND bands = '[]'::jsonb)");
                    table.CheckConstraint("ck_service_catalogue_currency", "currency IS NOT NULL AND currency IN (643, 840)");
                    table.CheckConstraint("ck_service_catalogue_method", "price_method IN (0, 100, 200, 300, 400)");
                    table.CheckConstraint("ck_service_catalogue_parameters", "(price_method = 0 AND percentage IS NOT NULL AND percentage > 0 AND percentage <= 100\r\n    AND amount IS NULL AND currency IN (643, 840)\r\n    AND (minimum_amount IS NULL OR minimum_amount >= 0)\r\n    AND (maximum_amount IS NULL OR maximum_amount >= 0)\r\n    AND (minimum_amount IS NULL OR maximum_amount IS NULL OR maximum_amount >= minimum_amount))\r\nOR (price_method = 100 AND percentage IS NULL AND minimum_amount IS NULL AND maximum_amount IS NULL\r\n    AND amount IS NOT NULL AND amount >= 0 AND currency IN (643, 840))\r\nOR (price_method IN (200, 300, 400) AND percentage IS NULL AND minimum_amount IS NULL AND maximum_amount IS NULL\r\n    AND amount IS NULL AND currency IN (643, 840))");
                    table.CheckConstraint("ck_service_catalogue_period", "available_from IS NULL OR available_by IS NULL OR available_by >= available_from");
                    table.CheckConstraint("ck_service_catalogue_product_identity", "(service = 0 AND id = 1) OR (service <> 0 AND id <> 1)");
                    table.CheckConstraint("ck_service_catalogue_service", "service IN (0, 100, 200, 300, 400, 500, 600, 700)");
                    table.CheckConstraint("ck_service_catalogue_timestamps", "updated_at >= created_at");
                    table.CheckConstraint("ck_service_catalogue_version", "version <> '00000000-0000-0000-0000-000000000000'::uuid");
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_pricing_order_id",
                table: "order_pricing_snapshots",
                columns: new[] { "order_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_order_pricing_snapshots_actor_id",
                table: "order_pricing_snapshots",
                column: "actor_id");

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

            migrationBuilder.Sql("""
                WITH seed AS (
                    SELECT date_trunc('microseconds', clock_timestamp()) AS seeded_at,
                           gen_random_uuid() AS seeded_version
                )
                INSERT INTO service_catalogue_entries
                    (id, service, price_method, percentage, minimum_amount, maximum_amount, amount,
                     currency, interval_currency, bands, available_from, available_by,
                     created_at, updated_at, version)
                SELECT 1, 0, 200, NULL, NULL, NULL, NULL, 840, NULL, '[]'::jsonb, NULL, NULL,
                       seeded_at, seeded_at, seeded_version
                FROM seed;
                SELECT setval(pg_get_serial_sequence('service_catalogue_entries', 'id'), 1, true);
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION protect_product_catalogue_entry() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.id = 1 OR NEW.service = 0 THEN
                            RAISE EXCEPTION 'The product catalogue entry is immutable'
                                USING ERRCODE = '23514', CONSTRAINT = 'ck_service_catalogue_product_immutable';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF OLD.id = 1 OR OLD.service = 0 THEN
                        RAISE EXCEPTION 'The product catalogue entry is immutable'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_service_catalogue_product_immutable';
                    END IF;
                    IF TG_OP = 'UPDATE' THEN
                        RETURN NEW;
                    END IF;
                    RETURN OLD;
                END;
                $$;
                CREATE TRIGGER tr_service_catalogue_product_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON service_catalogue_entries
                    FOR EACH ROW EXECUTE FUNCTION protect_product_catalogue_entry();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_pricing_snapshots");

            migrationBuilder.DropTable(
                name: "service_catalogue_audit_events");

            migrationBuilder.DropTable(
                name: "service_catalogue_entries");

            migrationBuilder.Sql("DROP FUNCTION valid_service_price_bands(jsonb);");
            migrationBuilder.Sql("DROP FUNCTION protect_product_catalogue_entry();");

        }
    }
}
