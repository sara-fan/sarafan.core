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
    public partial class _0_3_7_Payments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_information_bundles",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    recipient_type = table.Column<int>(type: "integer", nullable: true),
                    recipient_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    inn = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    kpp = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    settlement_account = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    bank_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    bik = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    correspondent_account = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    payment_link = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    qr_content = table.Column<byte[]>(type: "bytea", nullable: true),
                    qr_content_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    qr_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<int>(type: "integer", nullable: false),
                    updated_by = table.Column<int>(type: "integer", nullable: false),
                    published = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_information_bundles", x => x.id);
                    table.CheckConstraint("ck_payment_bundles_enabled", "NOT enabled OR published");
                    table.CheckConstraint("ck_payment_bundles_timestamps", "updated_at >= created_at");
                    table.ForeignKey(
                        name: "FK_payment_information_bundles_backoffice_users_created_by",
                        column: x => x.created_by,
                        principalTable: "backoffice_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payment_information_bundles_backoffice_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "backoffice_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_information_bundles_created_by",
                table: "payment_information_bundles",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_payment_information_bundles_updated_by",
                table: "payment_information_bundles",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "ux_payment_bundles_enabled",
                table: "payment_information_bundles",
                column: "enabled",
                unique: true,
                filter: "enabled = TRUE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_information_bundles");
        }
    }
}
