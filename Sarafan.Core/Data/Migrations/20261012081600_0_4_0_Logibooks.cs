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
    public partial class _0_4_0_Logibooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "logibooks_registers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    target = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    mapping_version = table.Column<int>(type: "integer", nullable: false),
                    intent_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    request = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    phase = table.Column<int>(type: "integer", nullable: false),
                    actor_id = table.Column<int>(type: "integer", nullable: false),
                    actor_name = table.Column<string>(type: "character varying(605)", maxLength: 605, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    authentication_retried = table.Column<bool>(type: "boolean", nullable: false),
                    investigation_required = table.Column<bool>(type: "boolean", nullable: false),
                    remote_register_id = table.Column<int>(type: "integer", nullable: true),
                    failure = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_logibooks_registers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "logibooks_receipts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    reconciler_id = table.Column<int>(type: "integer", nullable: true),
                    reconciler_name = table.Column<string>(type: "character varying(605)", maxLength: 605, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_logibooks_receipts", x => x.id);
                    table.ForeignKey(
                        name: "FK_logibooks_receipts_logibooks_registers_register_id",
                        column: x => x.register_id,
                        principalTable: "logibooks_registers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "logibooks_reconciliations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<int>(type: "integer", nullable: false),
                    actor_name = table.Column<string>(type: "character varying(605)", maxLength: 605, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_logibooks_reconciliations", x => x.id);
                    table.ForeignKey(
                        name: "FK_logibooks_reconciliations_logibooks_registers_register_id",
                        column: x => x.register_id,
                        principalTable: "logibooks_registers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "logibooks_register_rows",
                columns: table => new
                {
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    order_id = table.Column<long>(type: "bigint", nullable: false),
                    order_number = table.Column<string>(type: "character varying(29)", maxLength: 29, nullable: false),
                    expected_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    blocking = table.Column<bool>(type: "boolean", nullable: false),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    parcel_id = table.Column<int>(type: "integer", nullable: true),
                    status_id = table.Column<int>(type: "integer", nullable: true),
                    check_status = table.Column<int>(type: "integer", nullable: true),
                    issues = table.Column<string>(type: "jsonb", nullable: false),
                    acceptance_history_event_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_logibooks_register_rows", x => new { x.register_id, x.row_number });
                    table.ForeignKey(
                        name: "FK_logibooks_register_rows_logibooks_registers_register_id",
                        column: x => x.register_id,
                        principalTable: "logibooks_registers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_logibooks_register_rows_order_history_events_acceptance_his~",
                        column: x => x.acceptance_history_event_id,
                        principalTable: "order_history_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_logibooks_register_rows_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_logibooks_receipts_register_id_attempt_id_hash",
                table: "logibooks_receipts",
                columns: new[] { "register_id", "attempt_id", "hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_logibooks_reconciliations_register_id",
                table: "logibooks_reconciliations",
                column: "register_id");

            migrationBuilder.CreateIndex(
                name: "IX_logibooks_register_rows_acceptance_history_event_id",
                table: "logibooks_register_rows",
                column: "acceptance_history_event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_logibooks_blocking_order",
                table: "logibooks_register_rows",
                column: "order_id",
                unique: true,
                filter: "blocking");

            migrationBuilder.CreateIndex(
                name: "IX_logibooks_registers_phase_created_at",
                table: "logibooks_registers",
                columns: new[] { "phase", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "logibooks_receipts");

            migrationBuilder.DropTable(
                name: "logibooks_reconciliations");

            migrationBuilder.DropTable(
                name: "logibooks_register_rows");

            migrationBuilder.DropTable(
                name: "logibooks_registers");
        }
    }
}
