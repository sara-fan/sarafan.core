// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class OrderPricingTests
{
    private Task<Sarafan.Core.RestModels.OrderHistoryPageDto> History(int page = 1, int pageSize = 25,
        string sort = "timestamp", string direction = "desc", string? search = null, int? area = null,
        int? actor = null, string? from = null, string? to = null)
        => service.HistoryAsync("12345678-1", Admin, page, pageSize, sort, direction, search, area, actor, from, to, default);

    [Test]
    public async Task UnifiedPricingHistoryRetainsDetailsAndFrozenStatusWithoutDuplicates()
    {
        var ops = await service.HistoryOperationsAsync("12345678-1", Admin, default);
        Assert.That(ops.Kinds, Has.Length.EqualTo(5));
        var saved = await service.UpdatePricingAsync("12345678-1", new(order.UpdatedAt, Sarafan.Core.RestModels.OrderPricingInputs.Empty), actorId, Shift, default);
        await service.ConfirmPricingAsync("12345678-1", new(saved.UpdatedAt), actorId, Shift, default);
        var page = await History();
        Assert.That(page.Items, Has.Length.EqualTo(3)); // A truthful creation placeholder, calculation and confirmation.
        var confirmed = page.Items.Single(item => item.Kind == OrderHistoryKind.QuoteConfirmed);
        var details = await service.HistoryDetailAsync("12345678-1", confirmed.EventKey, Admin, default);
        Assert.That(details.PricingBefore!.TotalRub, Is.EqualTo(saved.Calculation.TotalRub));
        Assert.That(details.PricingAfter!.TotalRub, Is.EqualTo(saved.Calculation.TotalRub));
        Assert.That(details.ValidUntilAfter, Is.EqualTo(Now.AddHours(24)));
        Assert.That(details.StatusBefore, Is.EqualTo(OrderStatus.UnderReview));
        Assert.That(details.StatusAfter, Is.EqualTo(OrderStatus.QuoteReady));
        Assert.That(details.Event.ActorName, Is.EqualTo("Иванов Иван"));
        var initial = await service.HistoryDetailAsync("12345678-1", "3-1", Admin, default);
        Assert.That(initial.MissingCreationDetails, Is.True);
        Assert.That(initial.ProductAfter, Is.Null);
        var entity = await db.Set<OrderHistoryEvent>().FirstAsync();
        entity.ActorName = "altered";
        Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Test]
    public async Task LegacyEvidenceGroupsOnlyOneToOneAndReturnsAllPages()
    {
        var product = OrderService.CurrentProduct(order);
        var calculation = await OrderPriceCalculator.CalculateAsync(db, order, Now, Sarafan.Core.RestModels.OrderPricingInputs.Empty, null, default);
        var audit = new OrderProductAuditEvent { Order = order, Kind = OrderProductAuditKind.Created, OccurredAt = Now, After = JsonSerializer.Serialize(product) };
        db.Add(audit);
        db.Add(new OrderPricingSnapshot { Order = order, At = Now, Payload = JsonSerializer.Serialize(calculation, WebJson) });
        await db.SaveChangesAsync();
        var grouped = await History();
        Assert.That(grouped.Items, Has.Length.EqualTo(1));
        var detail = await service.HistoryDetailAsync("12345678-1", grouped.Items[0].EventKey, Admin, default);
        Assert.That(detail.ProductAfter, Is.EqualTo(product));
        Assert.That(detail.PricingAfter, Is.Not.Null);
        for (var i = 1; i <= 105; i++) db.Add(new OrderPricingSnapshot { Order = order, At = Now.AddSeconds(i), Payload = JsonSerializer.Serialize(calculation, WebJson) });
        await db.SaveChangesAsync();
        var page = await History(pageSize: 100);
        Assert.That(page.Pagination.TotalCount, Is.EqualTo(106));
        Assert.That(page.Items, Has.Length.EqualTo(100));
        Assert.That((await History(page: 2, pageSize: 100)).Items, Has.Length.EqualTo(6));
        Assert.That((await History(page: int.MaxValue, pageSize: 100)).Items, Is.Empty);
        db.Add(new OrderPricingSnapshot { Order = order, At = Now, Payload = JsonSerializer.Serialize(calculation, WebJson) });
        await db.SaveChangesAsync();
        Assert.That((await History()).Pagination.TotalCount, Is.EqualTo(108)); // Ambiguous pair becomes three separate records.
    }

    [TestCase("timestamp", "asc")]
    [TestCase("timestamp", "desc")]
    [TestCase("actor", "asc")]
    [TestCase("actor", "desc")]
    [TestCase("event", "asc")]
    [TestCase("event", "desc")]
    public async Task HistorySortsFiltersAndEchoesNormalizedQuery(string sort, string direction)
    {
        await service.UpdatePricingAsync("12345678-1", new(order.UpdatedAt, Sarafan.Core.RestModels.OrderPricingInputs.Empty), actorId, Shift, default);
        var result = await History(sort: sort, direction: direction, search: "  иВАН  ", area: 4, actor: 100, from: "2026-09-24", to: "2026-09-24");
        Assert.That(result.Items, Has.Length.EqualTo(1));
        Assert.That(result.Search, Is.EqualTo("иВАН"));
        Assert.That(result.Sorting.SortBy, Is.EqualTo(sort));
        Assert.That((await History(from: "2026-09-25")).Items, Is.Empty);
        Assert.That((await History(to: "2026-09-23")).Items, Is.Empty);
    }

    [TestCase(0, 25, "timestamp", "desc", null, null, null, null)]
    [TestCase(1, 11, "timestamp", "desc", null, null, null, null)]
    [TestCase(1, 25, "unknown", "desc", null, null, null, null)]
    [TestCase(1, 25, "timestamp", "unknown", null, null, null, null)]
    [TestCase(1, 25, "timestamp", "desc", 3, null, null, null)]
    [TestCase(1, 25, "timestamp", "desc", null, 1, null, null)]
    [TestCase(1, 25, "timestamp", "desc", null, null, "invalid", null)]
    [TestCase(1, 25, "timestamp", "desc", null, null, null, "invalid")]
    [TestCase(1, 25, "timestamp", "desc", null, null, "2026-09-25", "2026-09-24")]
    [TestCase(1, 25, "timestamp", "desc", null, null, "0001-01-01", null)]
    [TestCase(1, 25, "timestamp", "desc", null, null, null, "9999-12-31")]
    public void HistoryRejectsInvalidQueries(int page, int size, string sort, string direction, int? area, int? actor, string? from, string? to)
        => Assert.That(Assert.ThrowsAsync<ServiceException>(() => History(page, size, sort, direction, area: area, actor: actor, from: from, to: to))!.Code, Is.EqualTo("invalid_order_list_filter"));

    [TestCase("bad")]
    [TestCase("0-01")]
    [TestCase("4-1")]
    [TestCase("0-0")]
    [TestCase("x-1")]
    [TestCase("0-x")]
    [TestCase("0-999")]
    public void HistoryRejectsMissingAndInvalidKeys(string key)
        => Assert.That(Assert.ThrowsAsync<ServiceException>(() => service.HistoryDetailAsync("12345678-1", key, Admin, default))!.Code, Is.EqualTo("resource_not_found"));

    [Test]
    public void HistoryAuthorizationAndQueryTranslationRequireNoDatabaseConnection()
    {
        Assert.That(Assert.ThrowsAsync<ServiceException>(() => service.HistoryOperationsAsync("12345678-1", [], default))!.Code, Is.EqualTo("access_denied"));
        Assert.That(Assert.ThrowsAsync<ServiceException>(() => service.HistoryAsync("12345678-1", [], 1, 25, "timestamp", "desc", null, null, null, null, null, default))!.Code, Is.EqualTo("access_denied"));
        Assert.That(Assert.ThrowsAsync<ServiceException>(() => service.HistoryDetailAsync("12345678-1", "3-1", [], default))!.Code, Is.EqualTo("access_denied"));
        Assert.That(Assert.ThrowsAsync<ServiceException>(() => History(search: new string('x', 201)))!.Code, Is.EqualTo("invalid_order_list_filter"));
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=metadata;Username=unused;Password=unused").Options);
        var disconnected = new OrderService(context, null!, null!, null!, null!, null!, new Clock(), NullLogger<OrderService>.Instance);
        var sql = disconnected.HistoryQuery(1).OrderBy(row => row.At).Take(25).ToQueryString();
        Assert.That(sql, Does.Contain("UNION ALL"));
        Assert.That(sql, Does.Contain("LIMIT"));
    }
    [TestCase(BackofficeRoles.Administrator)]
    [TestCase(BackofficeRoles.ShiftManager)]
    [TestCase(BackofficeRoles.SeniorOperator)]
    [TestCase(BackofficeRoles.Operator)]
    public async Task AllStaffReadLegacyProductAndPricingEvidence(string role)
    {
        var product = OrderService.CurrentProduct(order);
        var calculation = await OrderPriceCalculator.CalculateAsync(db, order, Now, Sarafan.Core.RestModels.OrderPricingInputs.Empty, null, default);
        db.Add(new OrderProductAuditEvent
        {
            Order = order,
            Kind = OrderProductAuditKind.StaffCorrected,
            ActorId = actorId,
            OccurredAt = Now,
            Before = JsonSerializer.Serialize(product),
            After = JsonSerializer.Serialize(product with { Quantity = 3 })
        });
        db.Add(new OrderPricingSnapshot
        {
            Order = order,
            At = Now.AddSeconds(1),
            ValidUntil = Now.AddHours(24),
            ActorId = actorId,
            ActorName = "Сохранённое имя",
            Payload = JsonSerializer.Serialize(calculation, WebJson)
        });
        db.Add(new OrderProductAuditEvent { Order = order, Kind = OrderProductAuditKind.Parsed, OccurredAt = Now.AddSeconds(-1), After = JsonSerializer.Serialize(product) });
        await db.SaveChangesAsync();
        var page = await service.HistoryAsync("12345678-1", [role], 1, 25, "timestamp", "desc", null, null, null, null, null, default);
        Assert.That(page.Items, Has.Length.EqualTo(4));
        var correction = page.Items.Single(item => item.Kind == OrderHistoryKind.ProductChanged);
        Assert.That(correction.ActorNameHistorical, Is.False);
        var corrected = await service.HistoryDetailAsync("12345678-1", correction.EventKey, [role], default);
        Assert.That(corrected.ProductBefore, Is.EqualTo(product));
        Assert.That(corrected.ProductAfter!.Quantity, Is.EqualTo(3));
        var quote = page.Items.Single(item => item.Kind == OrderHistoryKind.QuoteConfirmed);
        var confirmed = await service.HistoryDetailAsync("12345678-1", quote.EventKey, [role], default);
        Assert.That(confirmed.StatusAfter, Is.EqualTo(OrderStatus.QuoteReady));
        Assert.That(confirmed.Event.ActorName, Is.EqualTo("Сохранённое имя"));
    }

}
