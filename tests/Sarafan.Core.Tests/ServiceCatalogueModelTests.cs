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
            (ServiceKind.Product, 0, "Товар", "product"),
            (ServiceKind.UsWarehouseExpenses, 100, "Расходы до склада в США", "us-warehouse-expenses"),
            (ServiceKind.InternationalDelivery, 200, "Доставка из США в Россию", "international-delivery"),
            (ServiceKind.DomesticDelivery, 300, "Доставка по России", "domestic-delivery"),
            (ServiceKind.ServiceCommission, 400, "Комиссия/маржа «Сарафана»", "service-commission"),
            (ServiceKind.WarehousePhoto, 500, "Фото товара на складе в США", "warehouse-photo"),
            (ServiceKind.ProductInspection, 600, "Проверка товара", "product-inspection"),
            (ServiceKind.ShipmentInsurance, 700, "Страхование отправления", "shipment-insurance")
        };
        var methods = new[]
        {
            (PriceMethod.Percent, 0, "Процент от цены товара", "percent"),
            (PriceMethod.Fixed, 100, "Фиксированная стоимость", "fixed"),
            (PriceMethod.Manual, 200, "Ввод вручную", "manual")
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
            Assert.That(entry.GetCheckConstraints().Select(item => item.Name), Does.Contain("ck_service_catalogue_parameters"));
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
            "20260921155120_0_3_0_ServiceCatalogue");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(script, Does.Contain("ex_service_catalogue_entries_service_period"));
            Assert.That(script, Does.Contain("int4range(service, service, '[]') WITH &&"));
            Assert.That(script, Does.Contain("daterange(available_from, available_by, '[]') WITH &&"));
            Assert.That(script, Does.Not.Contain("CREATE EXTENSION"));
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
