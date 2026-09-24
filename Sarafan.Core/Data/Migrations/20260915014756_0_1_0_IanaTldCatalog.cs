// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sarafan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class _0_1_0_IanaTldCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "iana_tld_catalog",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    version = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    source_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retrieved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    top_level_domains = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_iana_tld_catalog", x => x.id);
                    table.CheckConstraint("ck_iana_tld_catalog_content_sha256", "content_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_iana_tld_catalog_not_empty", "cardinality(top_level_domains) > 0");
                    table.CheckConstraint("ck_iana_tld_catalog_singleton", "id = 1");
                    table.CheckConstraint("ck_iana_tld_catalog_version", "version ~ '^[0-9]{10}$'");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "iana_tld_catalog");
        }
    }
}
