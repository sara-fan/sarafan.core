// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sarafan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class _0_0_9_ConsentListPaginationIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_legal_document_audit_events_at",
                table: "legal_document_audit_events");

            migrationBuilder.CreateIndex(
                name: "IX_legal_document_audit_events_at_id",
                table: "legal_document_audit_events",
                columns: new[] { "at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_consent_withdrawal_requests_queue",
                table: "customer_consent_withdrawal_requests",
                columns: new[] { "processed", "requested_at", "customer_id" },
                descending: new[] { false, true, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_legal_document_audit_events_at_id",
                table: "legal_document_audit_events");

            migrationBuilder.DropIndex(
                name: "IX_customer_consent_withdrawal_requests_queue",
                table: "customer_consent_withdrawal_requests");

            migrationBuilder.CreateIndex(
                name: "IX_legal_document_audit_events_at",
                table: "legal_document_audit_events",
                column: "at");
        }
    }
}
