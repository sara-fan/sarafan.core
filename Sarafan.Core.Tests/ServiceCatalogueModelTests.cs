// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Sarafan.Core.Data;
using Sarafan.Core.Models;

namespace Sarafan.Core.Tests;

[TestFixture]
public sealed class ServiceCatalogueModelTests
{
    private const string MetadataConnection = "Host=127.0.0.1;Port=1;Database=model_metadata_only;Username=unused;Password=unused;Timeout=1";

    private static AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(MetadataConnection).Options);

    [Test]
    public void EnumsHaveStableValuesNamesAndAliases()
    {
        var services = new[]
        {
            (ServiceKind.Product, 0, "Выкуп товара", "product"),
            (ServiceKind.UsWarehouseExpenses, 100, "Доставка до склада в США", "us-warehouse-delivery"),
            (ServiceKind.InternationalDelivery, 200, "Доставка из США в Россию", "international-delivery"),
            (ServiceKind.DomesticDelivery, 300, "Доставка по России", "domestic-delivery"),
            (ServiceKind.ServiceCommission, 400, "Комиссия сервиса", "service-commission"),
            (ServiceKind.WarehousePhoto, 500, "Фото товара на складе в США", "warehouse-photo"),
            (ServiceKind.ProductInspection, 600, "Проверка товара", "product-inspection"),
            (ServiceKind.ShipmentInsurance, 700, "Страхование отправления", "shipment-insurance")
        };
        var methods = new[]
        {
            (PriceMethod.Percent, 0, "Процент от цены товара", "percent"),
            (PriceMethod.Fixed, 100, "Фиксированная стоимость", "fixed"),
            (PriceMethod.Manual, 200, "Ввод вручную", "manual"),
            (PriceMethod.Auto, 300, "Автоматическое определение", "auto"),
            (PriceMethod.Stepped, 400, "Стоимость по диапазонам", "stepped")
        };
        var actions = new[]
        {
            (ServiceCatalogueAuditAction.Created, 0, "Создано", "created"),
            (ServiceCatalogueAuditAction.Updated, 100, "Изменено", "updated"),
            (ServiceCatalogueAuditAction.Deleted, 200, "Удалено", "deleted")
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Enum.GetValues<ServiceKind>(), Is.EqualTo(services.Select(item => item.Item1)));
            Assert.That(services.Select(item => ((int)item.Item1, item.Item1.GetDisplayName(), item.Item1.GetRouteAlias())),
                Is.EqualTo(services.Select(item => (item.Item2, item.Item3, item.Item4))));
            Assert.That(methods.Select(item => ((int)item.Item1, item.Item1.GetDisplayName(), item.Item1.GetRouteAlias())),
                Is.EqualTo(methods.Select(item => (item.Item2, item.Item3, item.Item4))));
            Assert.That(actions.Select(item => ((int)item.Item1, item.Item1.GetDisplayName(), item.Item1.GetRouteAlias())),
                Is.EqualTo(actions.Select(item => (item.Item2, item.Item3, item.Item4))));
            Assert.Throws<ArgumentOutOfRangeException>(() => ((ServiceKind)999).GetDisplayName());
            Assert.Throws<ArgumentOutOfRangeException>(() => ((PriceMethod)999).GetRouteAlias());
            Assert.Throws<ArgumentOutOfRangeException>(() => ((ServiceCatalogueAuditAction)999).GetDisplayName());
        }
    }

    [Test]
    public void ModelUsesExactPricingTypesConstraintsAndRetainedAuditRelationship()
    {
        using var context = Context();
        var model = context.GetService<IDesignTimeModel>().Model;
        var entry = model.FindEntityType(typeof(ServiceCatalogueEntry))!;
        var audit = model.FindEntityType(typeof(ServiceCatalogueAuditEvent))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.GetTableName(), Is.EqualTo("service_catalogue_entries"));
            Assert.That(entry.FindProperty(nameof(ServiceCatalogueEntry.Percentage))!.GetPrecision(), Is.EqualTo(7));
            Assert.That(entry.FindProperty(nameof(ServiceCatalogueEntry.Percentage))!.GetScale(), Is.EqualTo(4));
            Assert.That(entry.FindProperty(nameof(ServiceCatalogueEntry.Amount))!.GetPrecision(), Is.EqualTo(10));
            Assert.That(entry.FindProperty(nameof(ServiceCatalogueEntry.Amount))!.GetScale(), Is.EqualTo(2));
            Assert.That(entry.FindProperty(nameof(ServiceCatalogueEntry.Version))!.IsConcurrencyToken, Is.True);
            Assert.That(entry.FindProperty(nameof(ServiceCatalogueEntry.Bands))!.GetColumnType(), Is.EqualTo("jsonb"));
            Assert.That(entry.FindProperty(nameof(ServiceCatalogueEntry.AvailableFrom))!.IsNullable, Is.True);
            Assert.That(entry.FindProperty(nameof(ServiceCatalogueEntry.IntervalCurrency)), Is.Not.Null);
            Assert.That(entry.GetCheckConstraints().Single(item => item.Name == "ck_service_catalogue_bands").Sql,
                Does.Contain("interval_currency IS NOT NULL AND interval_currency = 840"));
            Assert.That(entry.GetCheckConstraints().Single(item => item.Name == "ck_service_catalogue_parameters").Sql,
                Does.Contain("price_method = 0 AND percentage IS NOT NULL AND percentage > 0 AND percentage <= 100"));
            Assert.That(model.FindEntityType(typeof(OrderPricingSnapshot))!.GetTableName(), Is.EqualTo("order_pricing_snapshots"));
            Assert.That(entry.GetCheckConstraints().Select(item => item.Name), Does.Contain("ck_service_catalogue_parameters"));
            Assert.That(entry.GetCheckConstraints().Single(item => item.Name == "ck_service_catalogue_product_identity").Sql,
                Does.Contain("service = 0 AND id = 1"));
            Assert.That(entry.GetCheckConstraints().Single(item => item.Name == "ck_service_catalogue_currency").Sql,
                Does.Contain("643, 840"));
            Assert.That(audit.GetTableName(), Is.EqualTo("service_catalogue_audit_events"));
            Assert.That(audit.FindProperty(nameof(ServiceCatalogueAuditEvent.Before))!.GetColumnType(), Is.EqualTo("jsonb"));
            Assert.That(audit.FindProperty(nameof(ServiceCatalogueAuditEvent.After))!.GetColumnType(), Is.EqualTo("jsonb"));
            Assert.That(audit.GetForeignKeys().Any(key => key.PrincipalEntityType.ClrType == typeof(ServiceCatalogueEntry)), Is.False);
            Assert.That(audit.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(BackofficeUser)).DeleteBehavior,
                Is.EqualTo(DeleteBehavior.Restrict));
        }
    }

    [Test]
    public void MigrationAddsInclusivePostgreSqlExclusionConstraintWithoutOpeningAConnection()
    {
        using var context = Context();
        var script = context.GetService<IMigrator>().GenerateScript(
            "20260917204413_0_2_0_Stores",
            "20260924171050_0_3_0_ServiceCatalogue");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(script, Does.Contain("ex_service_catalogue_entries_service_period"));
            Assert.That(script, Does.Contain("int4range(service, service, '[]') WITH &&"));
            Assert.That(script, Does.Contain("daterange(available_from, available_by, '[]') WITH &&"));
            Assert.That(script, Does.Contain("available_from date,").And.Not.Contain("available_from date NOT NULL"));
            Assert.That(script, Does.Not.Contain("CREATE EXTENSION"));
            Assert.That(script, Does.Contain("price_method IN (0, 100, 200, 300, 400)"));
            Assert.That(script, Does.Contain("currency IS NOT NULL AND currency IN (643, 840)"));
            Assert.That(script, Does.Contain("interval_currency IS NOT NULL AND interval_currency = 840"));
            Assert.That(script, Does.Contain("AND amount IS NULL AND currency = 840"));
            Assert.That(script, Does.Contain("CREATE FUNCTION valid_service_price_bands"));
            Assert.That(script, Does.Contain("lower_value IS NULL OR lower_value <> previous_end"));
            Assert.That(script, Does.Contain("jsonb_typeof(band->'From') NOT IN ('number','null')"));
            Assert.That(script, Does.Contain("order_pricing_snapshots"));
            Assert.That(script, Does.Contain("ck_service_catalogue_product_identity"));
            Assert.That(script, Does.Contain("gen_random_uuid()"));
            Assert.That(script, Does.Contain("clock_timestamp()"));
            Assert.That(script, Does.Contain("SELECT 1, 0, 200, NULL, NULL, NULL, NULL, 840"));
            Assert.That(script, Does.Contain("pg_get_serial_sequence('service_catalogue_entries', 'id')"));
            Assert.That(script, Does.Contain("tr_service_catalogue_product_immutable"));
            Assert.That(script.Replace("\r\n", "\n"), Does.Contain("IF TG_OP = 'UPDATE' THEN\n        RETURN NEW;"));
        }
    }

    [Test]
    public void EntityInitializesAndAdvancesUtcMicrosecondTimestampsAndVersion()
    {
        var created = DateTimeOffset.Parse("2026-09-21T10:00:00.1234567+03:00");
        var entry = new ServiceCatalogueEntry(ServiceKind.Product, PriceMethod.Manual,
            null, null, null, null, Currency.Usd,
            new DateOnly(2026, 9, 21), null, created);
        var initialVersion = entry.Version;

        entry.Update(ServiceKind.Product, PriceMethod.Manual,
            null, null, null, null, Currency.Usd,
            new DateOnly(2026, 9, 21), null, created);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.CreatedAt.Offset, Is.EqualTo(TimeSpan.Zero));
            Assert.That(entry.CreatedAt.Ticks % 10, Is.Zero);
            Assert.That(entry.UpdatedAt, Is.EqualTo(entry.CreatedAt.AddMicroseconds(1)));
            Assert.That(entry.Version, Is.Not.EqualTo(initialVersion));
        }
    }
}
