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
    public partial class _0_2_0_Stores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stores",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    official_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    display_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stores", x => x.id);
                    table.CheckConstraint("ck_stores_description", "btrim(description) <> ''");
                    table.CheckConstraint("ck_stores_display_order", "display_order >= 0");
                    table.CheckConstraint("ck_stores_name", "btrim(name) <> ''");
                    table.CheckConstraint("ck_stores_official_url", "official_url ~* '^https?://'");
                    table.CheckConstraint("ck_stores_status", "status IN (0, 1, 2)");
                    table.CheckConstraint("ck_stores_timestamps", "updated_at >= created_at");
                    table.CheckConstraint("ck_stores_version", "version <> '00000000-0000-0000-0000-000000000000'::uuid");
                });

            migrationBuilder.CreateTable(
                name: "store_logos",
                columns: table => new
                {
                    store_id = table.Column<int>(type: "integer", nullable: false),
                    content_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_logos", x => x.store_id);
                    table.CheckConstraint("ck_store_logos_content_sha256", "content_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_store_logos_content_size", "octet_length(content) BETWEEN 1 AND 2097152");
                    table.CheckConstraint("ck_store_logos_content_type", "content_type IN ('image/png', 'image/jpeg', 'image/webp')");
                    table.ForeignKey(
                        name: "FK_store_logos_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stores_status_display_order_id",
                table: "stores",
                columns: new[] { "status", "display_order", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_stores_display_order",
                table: "stores",
                column: "display_order",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "store_logos");

            migrationBuilder.DropTable(
                name: "stores");
        }
    }
}
