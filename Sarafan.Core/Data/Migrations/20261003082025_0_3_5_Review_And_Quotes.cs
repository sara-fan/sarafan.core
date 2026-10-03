// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sarafan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class _0_3_5_Review_And_Quotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Lock customer source/destination and login receipts until the migration transaction commits.
            migrationBuilder.Sql("LOCK TABLE customers, customer_profiles, consent_onboarding IN ACCESS EXCLUSIVE MODE;");

            // The retired constraint may already have been removed manually.
            migrationBuilder.Sql("ALTER TABLE orders DROP CONSTRAINT IF EXISTS ck_orders_status;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_consent_onboarding_flow",
                table: "consent_onboarding");

            migrationBuilder.AddColumn<string>(
                name: "checkout_data",
                table: "orders",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "address",
                table: "customers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "city",
                table: "customers",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "customers",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "first_name",
                table: "customers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "inn",
                table: "customers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_name",
                table: "customers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "passport_issue_date",
                table: "customers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "passport_issued_by",
                table: "customers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "passport_number",
                table: "customers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "passport_series",
                table: "customers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "patronymic",
                table: "customers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "postal_code",
                table: "customers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // Preserve personal details before removing the dependent table. Account identity,
            // authentication state, timestamps and order numbering remain on the original row.
            migrationBuilder.Sql("""
                UPDATE customers AS customer
                SET
                    address = profile.address,
                    city = profile.city,
                    email = profile.email,
                    first_name = profile.first_name,
                    inn = profile.inn,
                    last_name = profile.last_name,
                    passport_issue_date = profile.passport_issue_date,
                    passport_issued_by = profile.passport_issued_by,
                    passport_number = profile.passport_number,
                    passport_series = profile.passport_series,
                    patronymic = profile.patronymic,
                    postal_code = profile.postal_code
                FROM customer_profiles AS profile
                WHERE profile.customer_id = customer.id;
                """);

            migrationBuilder.DropTable(name: "customer_profiles");

            migrationBuilder.AddCheckConstraint(
                name: "ck_consent_onboarding_flow",
                table: "consent_onboarding",
                sql: "((flow = 1 AND target_customer_id IS NOT NULL AND (terms_accepted OR personal_data_document_id IS NOT NULL)) OR (flow = 2 AND personal_data_document_id IS NOT NULL)) AND ((personal_data_document_id IS NULL AND personal_data_hash IS NULL AND personal_data_idempotency_key IS NULL) OR (personal_data_document_id IS NOT NULL AND personal_data_hash IS NOT NULL AND personal_data_idempotency_key IS NOT NULL)) AND ((terms_accepted AND terms_idempotency_key IS NOT NULL) OR (NOT terms_accepted AND terms_idempotency_key IS NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("LOCK TABLE customers, orders, consent_onboarding IN ACCESS EXCLUSIVE MODE;");

            // The previous schema cannot retain checkout snapshots or newer review statuses.
            // Refuse a destructive downgrade before copying or dropping any customer data.
            // The unchanged history table retains all evidence, including newer payload versions.
            migrationBuilder.Sql("""
                DO $rollback$
                BEGIN
                    IF EXISTS (SELECT 1 FROM orders WHERE checkout_data IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot roll back 0_3_5_Review_And_Quotes while orders have checkout snapshots.';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM orders
                        WHERE status NOT IN (0, 100, 200, 300, 310, 320, 330, 340, 360, 380, 400, 500)
                    ) THEN
                        RAISE EXCEPTION 'Cannot roll back 0_3_5_Review_And_Quotes while orders use statuses unavailable in the previous schema.';
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM consent_onboarding
                        WHERE flow = 1 AND personal_data_document_id IS NOT NULL
                    ) THEN
                        RAISE EXCEPTION 'Cannot roll back 0_3_5_Review_And_Quotes while login receipts contain personal-data consent evidence.';
                    END IF;
                END;
                $rollback$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_consent_onboarding_flow",
                table: "consent_onboarding");

            // Recreate a profile for every customer, including accounts created after Up,
            // and copy the latest personal details back before dropping the merged columns.
            migrationBuilder.CreateTable(
                name: "customer_profiles",
                columns: table => new
                {
                    customer_id = table.Column<int>(type: "integer", nullable: false),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    city = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    first_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    inn = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    last_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    passport_issue_date = table.Column<DateOnly>(type: "date", nullable: true),
                    passport_issued_by = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    passport_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    passport_series = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    patronymic = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    postal_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_profiles", x => x.customer_id);
                    table.ForeignKey(
                        name: "FK_customer_profiles_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO customer_profiles (
                    customer_id, address, city, email, first_name, inn, last_name,
                    passport_issue_date, passport_issued_by, passport_number,
                    passport_series, patronymic, postal_code)
                SELECT
                    id, address, city, email, first_name, inn, last_name,
                    passport_issue_date, passport_issued_by, passport_number,
                    passport_series, patronymic, postal_code
                FROM customers;
                """);

            migrationBuilder.DropColumn(
                name: "checkout_data",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "address",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "city",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "email",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "first_name",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "inn",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "last_name",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "passport_issue_date",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "passport_issued_by",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "passport_number",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "passport_series",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "patronymic",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "postal_code",
                table: "customers");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_status",
                table: "orders",
                sql: "status IN (0, 100, 200, 300, 310, 320, 330, 340, 360, 380, 400, 500)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_consent_onboarding_flow",
                table: "consent_onboarding",
                sql: "(flow = 1 AND target_customer_id IS NOT NULL AND personal_data_document_id IS NULL AND personal_data_hash IS NULL AND personal_data_idempotency_key IS NULL AND terms_accepted AND terms_idempotency_key IS NOT NULL) OR (flow = 2 AND personal_data_document_id IS NOT NULL AND personal_data_hash IS NOT NULL AND personal_data_idempotency_key IS NOT NULL AND ((terms_accepted AND terms_idempotency_key IS NOT NULL) OR (NOT terms_accepted AND terms_idempotency_key IS NULL)))");
        }
    }
}
