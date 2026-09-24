// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[TestFixture]
public sealed class ServiceCatalogueTests
{
    private static readonly string[] Admin = [BackofficeRoles.Administrator];
    private static readonly string[] Operator = [BackofficeRoles.Operator];
    private AppDbContext _database = null!;
    private CountingClock _clock = null!;
    private ServiceCatalogueService _service = null!;
    private int _actorId;

    [SetUp]
    public async Task SetUp()
    {
        _database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var actor = new BackofficeUser
        {
            Email = "catalogue@sarafan.test",
            NormalizedEmail = "catalogue@sarafan.test",
            FirstName = "Иван",
            LastName = "Иванов",
            Patronymic = "Иванович",
            PasswordHash = "unused",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _database.BackofficeUsers.Add(actor);
        await _database.SaveChangesAsync();
        _actorId = actor.Id;
        _clock = new CountingClock();
        _service = new ServiceCatalogueService(_database, _clock, NullLogger<ServiceCatalogueService>.Instance);
    }

    [TearDown]
    public void TearDown() => _database.Dispose();

    [Test]
    public void RulesAcceptOnlyTheThreeMethodShapesAndRejectEur()
    {
        Assert.DoesNotThrow(() => ServiceCatalogueRules.Prepare(Percent(), false));
        Assert.DoesNotThrow(() => ServiceCatalogueRules.Prepare(Fixed(Currency.Rub), false));
        Assert.DoesNotThrow(() => ServiceCatalogueRules.Prepare(Fixed(Currency.Usd), false));
        Assert.DoesNotThrow(() => ServiceCatalogueRules.Prepare(Manual(Currency.Rub), false));
        Assert.DoesNotThrow(() => ServiceCatalogueRules.Prepare(Manual(Currency.Usd), false));

        foreach (var request in new[] { Fixed(Currency.Eur), Manual(Currency.Eur), Fixed((Currency)999) })
            Rejected(() => ServiceCatalogueRules.Prepare(request, false), "invalid_service_catalogue_currency", "currency");

        var percentCurrency = Percent(); percentCurrency.Currency = Currency.Eur;
        Rejected(() => ServiceCatalogueRules.Prepare(percentCurrency, false), "invalid_service_catalogue_currency", "currency");
        var manualAmount = Manual(Currency.Rub); manualAmount.Amount = 0;
        Rejected(() => ServiceCatalogueRules.Prepare(manualAmount, false), "invalid_service_catalogue_amount", "amount");
        var fixedPercentage = Fixed(Currency.Rub); fixedPercentage.Percentage = 1;
        Rejected(() => ServiceCatalogueRules.Prepare(fixedPercentage, false), "invalid_service_catalogue_percentage", "percentage");
    }

    [Test]
    public void RulesEnforceDecimalDateAndVersionBoundaries()
    {
        foreach (var percentage in new decimal[] { 0, -1, 100.0001m, 1.12345m })
        {
            var request = Percent(); request.Percentage = percentage;
            Rejected(() => ServiceCatalogueRules.Prepare(request, false), "invalid_service_catalogue_percentage", "percentage");
        }
        foreach (var amount in new decimal[] { -0.01m, 100000000m, 1.001m })
        {
            var request = Fixed(Currency.Usd); request.Amount = amount;
            Rejected(() => ServiceCatalogueRules.Prepare(request, false), "invalid_service_catalogue_amount", "amount");
        }
        var reversed = Percent(); reversed.AvailableBy = reversed.AvailableFrom!.Value.AddDays(-1);
        Rejected(() => ServiceCatalogueRules.Prepare(reversed, false), "invalid_service_catalogue_dates", "availableBy");
        var undated = Percent(); undated.AvailableFrom = null; undated.AvailableBy = new DateOnly(2025, 12, 31);
        Assert.DoesNotThrow(() => ServiceCatalogueRules.Prepare(undated, false));
        var caps = Percent(); caps.MinimumAmount = 10; caps.MaximumAmount = 9;
        Rejected(() => ServiceCatalogueRules.Prepare(caps, false), "invalid_service_catalogue_maximum_amount", "maximumAmount");
        Rejected(() => ServiceCatalogueRules.RequireVersion(null), "invalid_service_catalogue_version", "version");
        Rejected(() => ServiceCatalogueRules.RequireVersion(Guid.Empty), "invalid_service_catalogue_version", "version");
    }

    [Test]
    public async Task UndatedStartCoversAllEarlierDatesAndRetainsTypedAudit()
    {
        var request = Percent();
        request.AvailableFrom = null;
        request.AvailableBy = new DateOnly(2026, 1, 31);
        var created = await _service.CreateAsync(request, _actorId, Admin, default);
        Assert.That(created.AvailableFrom, Is.Null);
        var overlapping = Fixed(Currency.Rub);
        overlapping.Service = created.Service;
        overlapping.AvailableFrom = new DateOnly(2026, 1, 31);
        await RejectedAsync(() => _service.CreateAsync(overlapping, _actorId, Admin, default), "service_catalogue_period_overlap", 409);
        overlapping.AvailableFrom = new DateOnly(2026, 2, 1);
        await _service.CreateAsync(overlapping, _actorId, Admin, default);
        var list = await _service.ListAsync(Operator, default);
        Assert.That(list.Items.Select(item => item.AvailableFrom), Is.EqualTo(new DateOnly?[] { new(2026, 2, 1), null }));
        var audit = await _service.AuditAsync(null, null, created.Id, null, 1, 10, "timestamp", "asc", Operator, default);
        Assert.That(audit.Items[0].After!.AvailableFrom, Is.Null);
    }

    [Test]
    public async Task CrudUsesInclusivePeriodsVersionsAndTypedRetainedAudit()
    {
        var firstRequest = Percent();
        firstRequest.AvailableBy = new DateOnly(2026, 1, 31);
        var first = await _service.CreateAsync(firstRequest, _actorId, Admin, default);
        Assert.That(_clock.ReadCount, Is.EqualTo(1));

        var touching = Fixed(Currency.Rub); touching.Service = first.Service; touching.AvailableFrom = new DateOnly(2026, 1, 31);
        await RejectedAsync(() => _service.CreateAsync(touching, _actorId, Admin, default), "service_catalogue_period_overlap", 409);
        var adjacent = Fixed(Currency.Rub); adjacent.Service = first.Service; adjacent.AvailableFrom = new DateOnly(2026, 2, 1);
        var second = await _service.CreateAsync(adjacent, _actorId, Admin, default);

        var update = Manual(Currency.Usd);
        update.Service = second.Service;
        update.AvailableFrom = second.AvailableFrom;
        update.Version = second.Version;
        var updated = await _service.UpdateAsync(second.Id, update, _actorId, Admin, default);
        Assert.That(updated.Version, Is.Not.EqualTo(second.Version));
        await RejectedAsync(() => _service.UpdateAsync(second.Id, update, _actorId, Admin, default),
            "service_catalogue_update_conflict", 409);

        await _service.DeleteAsync(first.Id, first.Version, _actorId, Admin, default);
        Assert.That(await _database.ServiceCatalogueEntries.AnyAsync(item => item.Id == first.Id), Is.False);
        var audit = await _service.AuditAsync(null, null, null, null, 1, 100,
            "timestamp", "asc", Operator, default);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(audit.Items, Has.Length.EqualTo(4));
            Assert.That(audit.Items[0].Action, Is.EqualTo(ServiceCatalogueAuditAction.Created));
            Assert.That(audit.Items[0].Before, Is.Null);
            Assert.That(audit.Items[0].After!.Id, Is.EqualTo(first.Id));
            Assert.That(audit.Items.Single(item => item.Action == ServiceCatalogueAuditAction.Updated).Before, Is.Not.Null);
            Assert.That(audit.Items.Single(item => item.Action == ServiceCatalogueAuditAction.Updated).After, Is.Not.Null);
            Assert.That(audit.Items.Single(item => item.Action == ServiceCatalogueAuditAction.Deleted).After, Is.Null);
            Assert.That(audit.Items.All(item => item.ActorName == "Иванов Иван Иванович"), Is.True);
        }
    }

    [Test]
    public async Task ServiceGuardsReadsAndMutationsAndValidatesAuditFilters()
    {
        Assert.That((await _service.ListAsync(Operator, default)).Items, Is.Empty);
        await RejectedAsync(() => _service.CreateAsync(Manual(Currency.Rub), _actorId, Operator, default), "access_denied", 403);
        await RejectedAsync(() => _service.ListAsync([], default), "access_denied", 403);
        await RejectedAsync(() => _service.AuditAsync(null, null, null, new string('x', 201),
            1, 10, "timestamp", "desc", Operator, default), "invalid_service_catalogue_audit_filter", 400);
        await RejectedAsync(() => _service.AuditAsync(null, null, null, null,
            1, 101, "timestamp", "desc", Operator, default), "invalid_service_catalogue_audit_filter", 400);
    }

    [Test]
    public async Task SeededProductIsImmutableAndIsTheOnlyProductDefinition()
    {
        var productRequest = Manual(Currency.Usd);
        productRequest.Service = ServiceKind.Product;
        productRequest.AvailableFrom = null;
        await RejectedAsync(() => _service.CreateAsync(productRequest, _actorId, Admin, default),
            "service_catalogue_product_reserved", 409);

        var now = DateTimeOffset.UtcNow;
        var seed = new ServiceCatalogueEntry(ServiceKind.Product, PriceMethod.Manual,
            null, null, null, null, Currency.Usd, null, null, now);
        _database.ServiceCatalogueEntries.Add(seed);
        _database.Entry(seed).Property(item => item.Id).CurrentValue = 1;
        await _database.SaveChangesAsync();

        productRequest.Version = seed.Version;
        await RejectedAsync(() => _service.UpdateAsync(1, productRequest, _actorId, Admin, default),
            "service_catalogue_product_reserved", 409);

        var changedService = Manual(Currency.Usd);
        changedService.Version = seed.Version;
        await RejectedAsync(() => _service.UpdateAsync(1, changedService, _actorId, Admin, default),
            "service_catalogue_product_reserved", 409);
        await RejectedAsync(() => _service.DeleteAsync(1, seed.Version, _actorId, Admin, default),
            "service_catalogue_product_reserved", 409);

        var other = await _service.CreateAsync(Manual(Currency.Usd), _actorId, Admin, default);
        productRequest.Version = other.Version;
        await RejectedAsync(() => _service.UpdateAsync(other.Id, productRequest, _actorId, Admin, default),
            "service_catalogue_product_reserved", 409);
        Assert.That(await _database.ServiceCatalogueEntries.CountAsync(item => item.Service == ServiceKind.Product), Is.EqualTo(1));
    }

    private static ServiceCatalogueWriteRequest Percent() => new()
    {
        Service = ServiceKind.WarehousePhoto,
        PriceMethod = PriceMethod.Percent,
        Percentage = 12.3456m,
        Currency = Currency.Rub,
        MinimumAmount = 1.25m,
        MaximumAmount = 90.50m,
        AvailableFrom = new DateOnly(2026, 1, 1)
    };

    private static ServiceCatalogueWriteRequest Fixed(Currency currency) => new()
    {
        Service = ServiceKind.ProductInspection,
        PriceMethod = PriceMethod.Fixed,
        Amount = 123.45m,
        Currency = currency,
        AvailableFrom = new DateOnly(2026, 1, 1)
    };

    private static ServiceCatalogueWriteRequest Manual(Currency currency) => new()
    {
        Service = ServiceKind.ShipmentInsurance,
        PriceMethod = PriceMethod.Manual,
        Currency = currency,
        AvailableFrom = new DateOnly(2026, 1, 1)
    };

    private static void Rejected(Action action, string code, string field)
    {
        var error = Assert.Throws<ServiceException>(action)!;
        Assert.That(error.Code, Is.EqualTo(code));
        Assert.That(error.Errors!.Keys, Does.Contain(field));
    }

    private static async Task RejectedAsync(Func<Task> action, string code, int status)
    {
        var error = Assert.ThrowsAsync<ServiceException>(async () => await action())!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.Code, Is.EqualTo(code));
            Assert.That(error.StatusCode, Is.EqualTo(status));
        }
    }

    private sealed class CountingClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-09-21T12:00:00.1234567Z");
        public int ReadCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            ReadCount++;
            _now = _now.AddMinutes(1);
            return _now;
        }
    }
}
