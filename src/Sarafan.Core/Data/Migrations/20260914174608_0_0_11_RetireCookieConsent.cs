// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sarafan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class _0_0_11_RetireCookieConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consent_associations");

            migrationBuilder.Sql("""
                DELETE FROM consent_replay_tombstones WHERE document_id IN (SELECT id FROM legal_documents WHERE kind = 0);
                DELETE FROM consent_events WHERE kind = 0 OR document_id IN (SELECT id FROM legal_documents WHERE kind = 0);
                DELETE FROM legal_document_audit_events WHERE kind = 0 OR document_id IN (SELECT id FROM legal_documents WHERE kind = 0);
                DELETE FROM legal_documents WHERE kind = 0;
                """);

            migrationBuilder.DropIndex(
                name: "IX_consent_events_idempotency_key",
                table: "consent_events");

            migrationBuilder.DropColumn(
                name: "cookie_categories",
                table: "legal_documents");

            migrationBuilder.DropColumn(
                name: "categories",
                table: "consent_events");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "consent_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => throw new NotSupportedException("Cookie-consent evidence was permanently deleted. Restore a verified backup to roll back this migration.");
    }
}
