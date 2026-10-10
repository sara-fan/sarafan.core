// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[TestFixture]
[NonParallelizable]
public sealed class ListDisplaySearchTests
{
    private AppDbContext db = null!;
    private Order order = null!;
    private BackofficeUser actor = null!;
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-26T21:15:00Z");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [SetUp]
    public async Task Setup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString());
        db = new AppDbContext(options.Options);
        await db.Database.EnsureCreatedAsync();
        actor = new() { Email = "search@example.test", NormalizedEmail = "SEARCH@EXAMPLE.TEST", FirstName = "Иван", LastName = "Ёлкин", PasswordHash = "unused", CreatedAt = At, UpdatedAt = At };
        var customer = new Customer { Phone = "+79991234567", CreatedAt = At, UpdatedAt = At };
        customer.AllocateOrderNumber("12345678");
        db.AddRange(actor, customer);
        await db.SaveChangesAsync();
        order = new(customer.Id, 1, "https://example.test/hidden-source-token", 3, null, Guid.NewGuid(), At);
        db.Add(order);
        db.Entry(order).Property(item => item.ProductName).CurrentValue = "Товар 100%_\\образец";
        db.Entry(order).Property(item => item.StoreName).CurrentValue = "Магазин";
        db.Entry(order).Property(item => item.SellerPrice).CurrentValue = 1234.50m;
        db.Entry(order).Property(item => item.SellerPriceCurrency).CurrentValue = Currency.Usd;
        db.Entry(order).Property(item => item.UpdatedAt).CurrentValue = At.AddHours(2);
        await db.SaveChangesAsync();
    }

    [TearDown]
    public async Task Cleanup()
    {
        if (db is not null) await db.DisposeAsync();
    }

    [TestCase("12345678-1", true)]
    [TestCase("ТОВАР", true)]
    [TestCase("магазин", true)]
    [TestCase("1 234,50$", true)]
    [TestCase("1\u00a0234,50", true)]
    [TestCase("  1\u202f234,50  ", true)]
    [TestCase("27.09.2026, 00:15 МСК", true)]
    [TestCase("27.09.2026, 02:15 МСК", true)]
    [TestCase("3", true)]
    [TestCase("100%_\\", true)]
    [TestCase("100X", false)]
    [TestCase("2026-09-26", false)]
    [TestCase("1234.50", false)]
    [TestCase("hidden-source-token", false)]
    [TestCase("Магазин 1", false)]
    [TestCase("", true)]
    public async Task OrdersMatchOnlyDisplayedValues(string search, bool matches)
        => Assert.That(await AppDatabaseOperations.For(db).ApplyOrderSearch(db.Orders, search).AnyAsync(), Is.EqualTo(matches));

    [Test]
    public async Task OrderStatusAndFallbacksAreSearchable()
    {
        Assert.That(await ListDisplaySearch.Orders(db.Orders, order.Status.GetDisplayName()).CountAsync(), Is.EqualTo(1));
        db.Entry(order).Property(item => item.ProductName).CurrentValue = null;
        db.Entry(order).Property(item => item.StoreName).CurrentValue = null;
        db.Entry(order).Property(item => item.SellerPrice).CurrentValue = null;
        db.Entry(order).Property(item => item.SellerPriceCurrency).CurrentValue = null;
        await db.SaveChangesAsync();
        foreach (var term in new[] { "Товар не указан", "Магазин не указан", "—" })
            Assert.That(await ListDisplaySearch.Orders(db.Orders, term).AnyAsync(), Is.True, term);
    }

    [Test]
    public async Task PrivacyAndLegalAuditSearchDisplayedFields()
    {
        db.Add(new CustomerConsentWithdrawalRequest { CustomerId = order.CustomerId, RequestedAt = At, Processed = false });
        var documentId = Guid.NewGuid();
        db.Add(new LegalDocumentAuditEvent
        {
            DocumentId = documentId,
            ActorId = actor.Id,
            Action = "created",
            At = At,
            Kind = LegalDocumentKind.UserAgreement,
            Title = "Правовой текст",
            DisplayVersion = "v3",
            EffectiveAt = At,
            SourceHash = "a",
            ContentHash = "b"
        });
        await db.SaveChangesAsync();
        foreach (var term in new[] { "№ " + order.CustomerId, "Ожидает ручной обработки", "27.09.2026, 00:15 МСК" })
            Assert.That(await ListDisplaySearch.Withdrawals(db.CustomerConsentWithdrawalRequests, term).AnyAsync(), Is.True, term);
        foreach (var term in new[] { "Создан", "Правовой текст", "Пользовательское соглашение", documentId.ToString()[..8], "v3", "27.09.2026", "ЁЛКИН ИВАН" })
            Assert.That(await ListDisplaySearch.LegalAudit(db.LegalDocumentAuditEvents, term).AnyAsync(), Is.True, term);
        Assert.That(await ListDisplaySearch.Withdrawals(db.CustomerConsentWithdrawalRequests, "false").AnyAsync(), Is.False);
        Assert.That(await ListDisplaySearch.LegalAudit(db.LegalDocumentAuditEvents, "created").AnyAsync(), Is.False);
    }

    [Test]
    public async Task UnknownOrderStatusIsSearchableByItsDisplayedFallback()
    {
        db.Entry(order).Property(row => row.Status).CurrentValue = (OrderStatus)999;
        await db.SaveChangesAsync();
        Assert.That(await ListDisplaySearch.Orders(db.Orders, "СТАТУС 999").CountAsync(), Is.EqualTo(1));
        Assert.That(await ListDisplaySearch.Orders(db.Orders, "На проверке").CountAsync(), Is.Zero);
    }

    [Test]
    public async Task HistoryFiltersAllRowsBeforePagingWithoutReadingEvidence()
    {
        for (var i = 0; i < 105; i++) db.Add(new OrderHistoryEvent
        {
            OrderId = order.Id,
            At = At.AddMinutes(i),
            Kind = OrderHistoryKind.ProductChanged,
            Areas = OrderHistoryArea.Product | OrderHistoryArea.Pricing,
            ActorType = OrderHistoryActor.Staff,
            ActorId = actor.Id,
            ActorName = "Ёлкин Иван",
            Payload = "{\"hidden\":\"secret-evidence\"}"
        });
        await db.SaveChangesAsync();
        var service = new OrderService(db, null!, null!, null!, null!, null!, TimeProvider.System, NullLogger<OrderService>.Instance);
        var rows = service.HistoryQuery(order.Id);
        foreach (var term in new[] { "Изменение товара", "ЁЛКИН", "Товар, Стоимость", "27.09.2026" })
            Assert.That(await ListDisplaySearch.History(rows, term).CountAsync(), Is.GreaterThanOrEqualTo(105), term);
        var filtered = ListDisplaySearch.History(rows, "Стоимость");
        Assert.That(await filtered.CountAsync(), Is.EqualTo(105));
        Assert.That(await filtered.OrderBy(row => row.At).ThenBy(row => row.Id).Skip(100).Take(10).CountAsync(), Is.EqualTo(5));
        Assert.That(await ListDisplaySearch.History(rows, "secret-evidence").CountAsync(), Is.Zero);
        Assert.That(await ListDisplaySearch.History(rows, "2026-09").CountAsync(), Is.Zero);
    }

    [TestCase(OrderHistoryArea.Customs, 1)]
    [TestCase(OrderHistoryArea.Pricing | OrderHistoryArea.Customs, 1)]
    [TestCase(OrderHistoryArea.Pricing, 0)]
    public async Task HistorySearchMatchesCustomsAreaOnlyWhenPresent(OrderHistoryArea areas, int count)
    {
        db.Add(new OrderHistoryEvent
        {
            OrderId = order.Id,
            At = At,
            Kind = OrderHistoryKind.ProductChanged,
            Areas = areas,
            ActorType = OrderHistoryActor.Staff,
            ActorId = actor.Id,
            ActorName = "Ёлкин Иван",
            Payload = "{}"
        });
        await db.SaveChangesAsync();
        var service = new OrderService(db, null!, null!, null!, null!, null!, TimeProvider.System, NullLogger<OrderService>.Instance);
        var rows = service.HistoryQuery(order.Id);
        foreach (var term in new[] { "Таможенные платежи", "ТАМОЖЕННЫЕ", "таможенные\u00a0платежи" })
            Assert.That(await ListDisplaySearch.History(rows, term).CountAsync(), Is.EqualTo(count), term);
    }

    [Test]
    public void PostgreSqlHistorySearchIncludesCustomsWithoutConnecting()
    {
        using var metadata = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=metadata;Username=unused").Options);
        var service = new OrderService(metadata, null!, null!, null!, null!, null!, TimeProvider.System, NullLogger<OrderService>.Instance);
        var sql = ListDisplaySearch.History(service.HistoryQuery(order.Id), "Таможенные платежи")
            .OrderBy(row => row.At).ThenBy(row => row.Id).Skip(10).Take(10).ToQueryString();
        Assert.That(sql, Does.Contain("Таможенные платежи, ").And.Contain("& 32").And.Contain("LIMIT").And.Contain("OFFSET"));
    }

    [TestCase(PriceMethod.Fixed)]
    [TestCase(PriceMethod.Percent)]
    [TestCase(PriceMethod.Manual)]
    [TestCase(PriceMethod.Auto)]
    [TestCase(PriceMethod.Stepped)]
    public async Task TariffAuditMatchesFormattedSnapshotsAndNeverHiddenIds(PriceMethod method)
    {
        var snapshot = new ServiceCatalogueSnapshotDto(987654321, ServiceKind.ServiceCommission, method, 12.3456m, 0, 2345.60m, 1234.50m,
            Currency.Usd, new DateOnly(2026, 9, 27), new DateOnly(2026, 10, 1), At, At, Guid.NewGuid(), Currency.Usd,
            [new(null, 100, 0), new(100, null, 1234.50m)]);
        var json = JsonSerializer.Serialize(snapshot, Json);
        db.Add(new ServiceCatalogueAuditEvent
        {
            EntryId = snapshot.Id,
            Service = snapshot.Service,
            Action = ServiceCatalogueAuditAction.Created,
            ActorId = actor.Id,
            ActorName = "Ёлкин Иван",
            At = At,
            After = json
        });
        await db.SaveChangesAsync();
        var expected = CatalogueDisplaySearch.Snapshot(json);
        var parameters = method switch
        {
            PriceMethod.Percent => "12,3456%, мин. 0,00$, макс. 2 345,60$",
            PriceMethod.Fixed => "1 234,50$",
            PriceMethod.Stepped => "До 100,00$: 0,00$; свыше 100,00$: 1 234,50$",
            _ => "$"
        };
        Assert.That(ListDisplaySearch.Normalize(expected), Does.Contain(ListDisplaySearch.Normalize(parameters)));
        foreach (var term in new[] { expected, "Создано", "Комиссия сервиса", "ЁЛКИН", "27.09.2026, 00:15 МСК", "27.09.2026 — 01.10.2026", "—" })
            Assert.That(await AppDatabaseOperations.For(db).ApplyServiceCatalogueAuditSearch(db, db.ServiceCatalogueAuditEvents, term).CountAsync(), Is.EqualTo(1), term);
        foreach (var term in new[] { "987654321", snapshot.Version.ToString(), "2026-09-27", "1234.50", "percentage" })
            Assert.That(await AppDatabaseOperations.For(db).ApplyServiceCatalogueAuditSearch(db, db.ServiceCatalogueAuditEvents, term).CountAsync(), Is.Zero, term);
    }

    [TestCase(null, null)]
    [TestCase("2026-09-27", null)]
    [TestCase(null, "2026-10-01")]
    public async Task TariffAvailabilityAndBeforeSummaryMatchDisplay(string? from, string? by)
    {
        var snapshot = new ServiceCatalogueSnapshotDto(123, ServiceKind.DomesticDelivery, PriceMethod.Percent, 10m, null, null, null,
            Currency.Usd, from is null ? null : DateOnly.Parse(from), by is null ? null : DateOnly.Parse(by), At, At, Guid.NewGuid());
        var json = JsonSerializer.Serialize(snapshot, Json);
        db.Add(new ServiceCatalogueAuditEvent
        {
            EntryId = 123,
            Service = snapshot.Service,
            Action = ServiceCatalogueAuditAction.Deleted,
            ActorId = actor.Id,
            ActorName = "Проверка 100%_\\",
            At = At,
            Before = json
        });
        await db.SaveChangesAsync();
        foreach (var term in new[] { CatalogueDisplaySearch.Snapshot(json), "10%", "Удалено", "100%_\\" })
            Assert.That(await AppDatabaseOperations.For(db).ApplyServiceCatalogueAuditSearch(db, db.ServiceCatalogueAuditEvents, term).CountAsync(), Is.EqualTo(1), term);
        Assert.That(await AppDatabaseOperations.For(db).ApplyServiceCatalogueAuditSearch(db, db.ServiceCatalogueAuditEvents, "100X").CountAsync(), Is.Zero);
    }

    [Test]
    public async Task LegacyActorNameAndAdditionalFiltersRemainEffective()
    {
        var rates = new[] { Currency.Usd, Currency.Eur }.Select(currency => new ExchangeRateHistory
        {
            Provider = "test",
            Source = "fixture",
            BaseCurrency = currency,
            QuoteCurrency = Currency.Rub,
            Nominal = 1,
            OfficialRate = 100,
            SourceEffectiveDate = new DateOnly(2026, 9, 27),
            RetrievedAt = At
        }).ToArray();
        db.AddRange(rates);
        await db.SaveChangesAsync();
        db.Add(new OrderProductAuditEvent
        {
            OrderId = order.Id,
            ActorId = actor.Id,
            Kind = OrderProductAuditKind.StaffCorrected,
            OccurredAt = At,
            Before = "{}",
            After = "{}",
            UsdRateId = rates[0].Id,
            EurRateId = rates[1].Id
        });
        await db.SaveChangesAsync();
        var service = new OrderService(db, null!, null!, null!, null!, null!, TimeProvider.System, NullLogger<OrderService>.Instance);
        var rows = service.HistoryQuery(order.Id);
        var matches = ListDisplaySearch.History(rows, "ЁЛКИН Иван");
        Assert.That(await matches.CountAsync(), Is.EqualTo(1));
        Assert.That(await matches.Where(row => row.ActorType == OrderHistoryActor.Customer).CountAsync(), Is.Zero);
        Assert.That(await matches.Where(row => row.At < At).CountAsync(), Is.Zero);
    }

    [Test]
    public void PostgreSqlSearchRemainsComposableWithoutConnecting()
    {
        using var metadata = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=metadata;Username=unused").Options);
        var sql = ListDisplaySearch.Orders(metadata.Orders.Where(row => row.Quantity == 3), "1 234,50")
            .OrderBy(row => row.Id).Skip(10).Take(10).ToQueryString();
        Assert.That(sql, Does.Contain("to_char").And.Contain("timezone").And.Contain("LIMIT").And.Contain("OFFSET"));
    }
}
