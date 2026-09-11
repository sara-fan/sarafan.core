// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sarafan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class _0_0_9_Login : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE customers
                ALTER COLUMN state TYPE integer
                USING CASE state
                    WHEN 'Preliminary' THEN 0
                    WHEN 'Complete' THEN 1
                    WHEN 'Disabled' THEN 2
                END;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "phone",
                table: "customers",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AddColumn<int>(
                name: "token_version",
                table: "customers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "personal_data_hash",
                table: "consent_onboarding",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<Guid>(
                name: "personal_data_document_id",
                table: "consent_onboarding",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "flow",
                table: "consent_onboarding",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<Guid>(
                name: "personal_data_idempotency_key",
                table: "consent_onboarding",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "target_customer_id",
                table: "consent_onboarding",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "terms_accepted",
                table: "consent_onboarding",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "terms_hash",
                table: "consent_onboarding",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "terms_idempotency_key",
                table: "consent_onboarding",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE consent_onboarding AS receipt
                SET terms_hash = document.content_hash,
                    terms_accepted = TRUE,
                    personal_data_idempotency_key = gen_random_uuid(),
                    terms_idempotency_key = gen_random_uuid()
                FROM legal_documents AS document
                WHERE document.id = receipt.terms_document_id;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE consent_onboarding ALTER COLUMN flow DROP DEFAULT;
                ALTER TABLE consent_onboarding ALTER COLUMN terms_accepted DROP DEFAULT;
                ALTER TABLE consent_onboarding ALTER COLUMN terms_hash DROP DEFAULT;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_customers_phone_russian",
                table: "customers",
                sql: "phone ~ '^\\+7[0-9]{10}$'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customers_state",
                table: "customers",
                sql: "state IN (0, 1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_consent_onboarding_target_customer_id",
                table: "consent_onboarding",
                column: "target_customer_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_consent_onboarding_flow",
                table: "consent_onboarding",
                sql: "(flow = 1 AND target_customer_id IS NOT NULL AND personal_data_document_id IS NULL AND personal_data_hash IS NULL AND personal_data_idempotency_key IS NULL AND terms_accepted AND terms_idempotency_key IS NOT NULL) OR (flow = 2 AND personal_data_document_id IS NOT NULL AND personal_data_hash IS NOT NULL AND personal_data_idempotency_key IS NOT NULL AND ((terms_accepted AND terms_idempotency_key IS NOT NULL) OR (NOT terms_accepted AND terms_idempotency_key IS NULL)))");

            migrationBuilder.AddForeignKey(
                name: "FK_consent_onboarding_customers_target_customer_id",
                table: "consent_onboarding",
                column: "target_customer_id",
                principalTable: "customers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM consent_onboarding WHERE flow = 1;");

            migrationBuilder.DropForeignKey(
                name: "FK_consent_onboarding_customers_target_customer_id",
                table: "consent_onboarding");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customers_phone_russian",
                table: "customers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customers_state",
                table: "customers");

            migrationBuilder.DropIndex(
                name: "IX_consent_onboarding_target_customer_id",
                table: "consent_onboarding");

            migrationBuilder.DropCheckConstraint(
                name: "ck_consent_onboarding_flow",
                table: "consent_onboarding");

            migrationBuilder.DropColumn(
                name: "token_version",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "flow",
                table: "consent_onboarding");

            migrationBuilder.DropColumn(
                name: "personal_data_idempotency_key",
                table: "consent_onboarding");

            migrationBuilder.DropColumn(
                name: "target_customer_id",
                table: "consent_onboarding");

            migrationBuilder.DropColumn(
                name: "terms_accepted",
                table: "consent_onboarding");

            migrationBuilder.DropColumn(
                name: "terms_hash",
                table: "consent_onboarding");

            migrationBuilder.DropColumn(
                name: "terms_idempotency_key",
                table: "consent_onboarding");

            migrationBuilder.Sql(
                """
                ALTER TABLE customers
                ALTER COLUMN state TYPE character varying(16)
                USING CASE state
                    WHEN 0 THEN 'Preliminary'
                    WHEN 1 THEN 'Complete'
                    WHEN 2 THEN 'Disabled'
                END;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "phone",
                table: "customers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(12)",
                oldMaxLength: 12);

            migrationBuilder.AlterColumn<string>(
                name: "personal_data_hash",
                table: "consent_onboarding",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "personal_data_document_id",
                table: "consent_onboarding",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
