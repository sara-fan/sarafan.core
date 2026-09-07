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
    public partial class VersionedCustomerConsents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_consents");

            migrationBuilder.CreateTable(
                name: "consent_rights_cases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<int>(type: "integer", nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    responsible_staff_id = table.Column<int>(type: "integer", nullable: true),
                    retention_basis = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    completion_evidence = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    extension_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    extended = table.Column<bool>(type: "boolean", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consent_rights_cases", x => x.id);
                    table.ForeignKey(
                        name: "FK_consent_rights_cases_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "legal_audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rights_case_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_id = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legal_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "legal_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    locale = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    display_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source = table.Column<byte[]>(type: "bytea", nullable: false),
                    html = table.Column<string>(type: "text", nullable: false),
                    source_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    renderer_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    cookie_categories = table.Column<string[]>(type: "text[]", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_by = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disposed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legal_documents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "consent_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    customer_id = table.Column<int>(type: "integer", nullable: true),
                    subject_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    categories = table.Column<string[]>(type: "text[]", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retain_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consent_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_consent_events_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_consent_events_legal_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "legal_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "consent_onboarding",
                columns: table => new
                {
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    phone_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    personal_data_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    terms_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    personal_data_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consent_onboarding", x => x.token_hash);
                    table.ForeignKey(
                        name: "FK_consent_onboarding_legal_documents_personal_data_document_id",
                        column: x => x.personal_data_document_id,
                        principalTable: "legal_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_consent_onboarding_legal_documents_terms_document_id",
                        column: x => x.terms_document_id,
                        principalTable: "legal_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "consent_associations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    consent_event_id = table.Column<long>(type: "bigint", nullable: false),
                    customer_id = table.Column<int>(type: "integer", nullable: false),
                    associated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    authentication_token_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consent_associations", x => x.id);
                    table.ForeignKey(
                        name: "FK_consent_associations_consent_events_consent_event_id",
                        column: x => x.consent_event_id,
                        principalTable: "consent_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_consent_associations_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_consent_associations_consent_event_id",
                table: "consent_associations",
                column: "consent_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_consent_associations_customer_id_consent_event_id",
                table: "consent_associations",
                columns: new[] { "customer_id", "consent_event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consent_events_customer_id_kind_id",
                table: "consent_events",
                columns: new[] { "customer_id", "kind", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_consent_events_document_id",
                table: "consent_events",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_consent_events_subject_key_idempotency_key",
                table: "consent_events",
                columns: new[] { "subject_key", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consent_onboarding_expires_at",
                table: "consent_onboarding",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_consent_onboarding_personal_data_document_id",
                table: "consent_onboarding",
                column: "personal_data_document_id");

            migrationBuilder.CreateIndex(
                name: "IX_consent_onboarding_terms_document_id",
                table: "consent_onboarding",
                column: "terms_document_id");

            migrationBuilder.CreateIndex(
                name: "IX_consent_rights_cases_customer_id_idempotency_key",
                table: "consent_rights_cases",
                columns: new[] { "customer_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_legal_documents_kind_locale_display_version",
                table: "legal_documents",
                columns: new[] { "kind", "locale", "display_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_legal_documents_kind_locale_effective_at",
                table: "legal_documents",
                columns: new[] { "kind", "locale", "effective_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consent_associations");

            migrationBuilder.DropTable(
                name: "consent_onboarding");

            migrationBuilder.DropTable(
                name: "consent_rights_cases");

            migrationBuilder.DropTable(
                name: "legal_audit_events");

            migrationBuilder.DropTable(
                name: "consent_events");

            migrationBuilder.DropTable(
                name: "legal_documents");

            migrationBuilder.CreateTable(
                name: "customer_consents",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    customer_id = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    document_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_consents", x => x.id);
                    table.ForeignKey(
                        name: "FK_customer_consents_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_consents_customer_id_type_document_version",
                table: "customer_consents",
                columns: new[] { "customer_id", "type", "document_version" },
                unique: true);
        }
    }
}
