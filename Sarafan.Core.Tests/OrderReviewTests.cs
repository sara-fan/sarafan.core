// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class OrderPricingTests
{
    [TestCase(" Нет доставки в Россию ", "Нет доставки в Россию")]
    [TestCase("<script>alert('text')</script>", "<script>alert('text')</script>")]
    public async Task NegativeReviewHasIndependentTerminalStatusReasonTimeAndHistory(string reason, string expected)
    {
        var result = await service.RejectReviewAsync("12345678-1", new() { ExpectedUpdatedAt = order.UpdatedAt, Reason = reason }, actorId, Shift, default);
        var customer = await service.GetAsync(order.CustomerId, "12345678-1", default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(OrderStatus.CannotDeliver));
            Assert.That(result.ReviewReason, Is.EqualTo(expected));
            Assert.That(result.ReviewCompletedAt, Is.EqualTo(order.UpdatedAt));
            Assert.That(result.CanEditProduct, Is.False);
            Assert.That(customer.ReviewReason, Is.EqualTo(expected));
            Assert.That(customer.ReviewCompletedAt, Is.EqualTo(result.ReviewCompletedAt));
            Assert.That(customer.CanCancel, Is.False);
            Assert.That(customer.CancelledAt, Is.Null);
            Assert.That(customer.EstimatedDelivery, Is.Null);
        });
        var history = await db.Set<OrderHistoryEvent>().SingleAsync();
        Assert.That(history.Kind, Is.EqualTo(OrderHistoryKind.ReviewRejected));
        var evidence = JsonSerializer.Deserialize<OrderHistoryEvidence>(history.Payload, WebJson)!;
        Assert.That(evidence.StatusBefore, Is.EqualTo(OrderStatus.UnderReview));
        Assert.That(evidence.StatusAfter, Is.EqualTo(OrderStatus.CannotDeliver));
        var details = await service.HistoryDetailAsync("12345678-1", $"0-{history.Id}", Shift, default);
        Assert.That(details.ReviewReason, Is.EqualTo(expected));
        var cancelled = await Assert.ThrowsAsync<ServiceException>(() => service.CancelAsync(order.CustomerId, "12345678-1", new() { ExpectedUpdatedAt = order.UpdatedAt }, default));
        Assert.That(cancelled!.Code, Is.EqualTo("order_not_cancellable"));
        var confirmation = await Assert.ThrowsAsync<ServiceException>(() => service.ConfirmPricingAsync("12345678-1", new(order.UpdatedAt), actorId, Shift, default));
        Assert.That(confirmation!.Code, Is.EqualTo("order_not_editable"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    public async Task NegativeReviewRequiresReasonWithoutWrites(string? reason)
    {
        var before = order.UpdatedAt;
        var error = await Assert.ThrowsAsync<ServiceException>(() => service.RejectReviewAsync("12345678-1", new() { ExpectedUpdatedAt = before, Reason = reason }, actorId, Shift, default));
        Assert.That(error!.Code, Is.EqualTo("invalid_review_reason"));
        Assert.That(order.Status, Is.EqualTo(OrderStatus.UnderReview));
        Assert.That(order.UpdatedAt, Is.EqualTo(before));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(), Is.Zero);
    }

    [Test]
    public async Task NegativeReviewChecksRoleVersionStateActorAndReasonLength()
    {
        var request = new RejectOrderReviewRequest { ExpectedUpdatedAt = order.UpdatedAt, Reason = "Причина" };
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.RejectReviewAsync("12345678-1", request, actorId, [BackofficeRoles.Operator], default)))!.StatusCode, Is.EqualTo(403));
        request.ExpectedUpdatedAt = order.UpdatedAt.AddTicks(1);
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.RejectReviewAsync("12345678-1", request, actorId, Shift, default)))!.Code, Is.EqualTo("order_update_conflict"));
        request.ExpectedUpdatedAt = order.UpdatedAt;
        request.Reason = new string('я', 2001);
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.RejectReviewAsync("12345678-1", request, actorId, Shift, default)))!.Code, Is.EqualTo("invalid_review_reason"));
        request.Reason = "Причина";
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.RejectReviewAsync("12345678-1", request, int.MaxValue, Shift, default)))!.Code, Is.EqualTo("backoffice_user_not_found"));
        order.UpdatePricing(Now, true); await db.SaveChangesAsync();
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.RejectReviewAsync("12345678-1", request, actorId, Shift, default)))!.Code, Is.EqualTo("order_not_editable"));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(), Is.Zero);
    }

    private OrderService At(DateTimeOffset at)
        => new(db, null!, null!, null!, null!, new OrderLimitService(db, new OffsetClock(at), NullLogger<OrderLimitService>.Instance),
            new OffsetClock(at), NullLogger<OrderService>.Instance, reviewOptions: Options.Create(new OrderReviewOptions { DeliveryMinimumDays = 10, DeliveryMaximumDays = 15 }));

    [Test]
    public async Task QuoteExpiryUsesExactDeadlinePersistsOneEventAndRetainsFinancialEvidence()
    {
        var saved = await service.UpdatePricingAsync("12345678-1", new(order.UpdatedAt, OrderPricingInputs.Empty), actorId, Shift, default);
        await service.ConfirmPricingAsync("12345678-1", new(saved.UpdatedAt), actorId, Shift, default);
        var snapshot = await db.OrderPricingSnapshots.OrderByDescending(row => row.Id).FirstAsync();
        var payload = snapshot.Payload;
        var deadline = snapshot.ValidUntil!.Value;
        var ready = await At(deadline.AddTicks(-1)).GetAsync(order.CustomerId, "12345678-1", default);
        Assert.That(ready.Status, Is.EqualTo(OrderStatus.QuoteReady));
        Assert.That(ready.ReviewCompletedAt, Is.EqualTo(order.UpdatedAt));
        Assert.That(ready.EstimatedDelivery, Is.EqualTo(new OrderDeliveryEstimateDto(10, 15)));
        var expired = (await At(deadline).ListAsync(order.CustomerId, default)).Single();
        Assert.That(expired.Status, Is.EqualTo(OrderStatus.QuoteExpired));
        Assert.That(expired.Pricing.State, Is.EqualTo(CustomerPricingState.Expired));
        Assert.That(expired.Pricing.TotalRub, Is.EqualTo(ready.Pricing.TotalRub));
        Assert.That(expired.EstimatedDelivery, Is.EqualTo(ready.EstimatedDelivery));
        await At(deadline.AddDays(1)).ExpireQuotesAsync(default);
        var history = await db.Set<OrderHistoryEvent>().Where(row => row.Kind == OrderHistoryKind.QuoteExpired).SingleAsync();
        Assert.That(history.At, Is.EqualTo(deadline));
        Assert.That(history.ActorType, Is.EqualTo(OrderHistoryActor.System));
        Assert.That((await db.Orders.SingleAsync()).Status, Is.EqualTo(OrderStatus.QuoteExpired));
        Assert.That((await db.OrderPricingSnapshots.OrderByDescending(row => row.Id).FirstAsync()).Payload, Is.EqualTo(payload));
        var customer = await At(deadline.AddDays(1)).GetAsync(order.CustomerId, "12345678-1", default);
        Assert.That(customer.CanCancel, Is.True);
        Assert.That(customer.ReviewCompletedAt, Is.EqualTo(ready.ReviewCompletedAt));
    }

    [Test]
    public async Task OnlyLatestConfirmedSnapshotExpiresAndTerminalOrdersStayTerminal()
    {
        var calculation = await OrderPriceCalculator.CalculateAsync(db, order, Now, OrderPricingInputs.Empty, null, default);
        db.Add(new OrderPricingSnapshot { Order = order, At = Now.AddDays(-1), ValidUntil = Now, Payload = JsonSerializer.Serialize(calculation, WebJson) });
        await db.SaveChangesAsync();
        db.Add(new OrderPricingSnapshot { Order = order, At = Now, ValidUntil = Now.AddDays(1), Payload = JsonSerializer.Serialize(calculation, WebJson) });
        order.UpdatePricing(Now, true); await db.SaveChangesAsync();
        await new QuoteExpiryJob(service).Execute(null!, default);
        Assert.That(order.Status, Is.EqualTo(OrderStatus.QuoteReady));
        order.CancelByCustomer(Now.AddHours(1)); await db.SaveChangesAsync();
        await At(Now.AddDays(2)).ExpireQuotesAsync(default);
        Assert.That(order.Status, Is.EqualTo(OrderStatus.Cancelled));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(), Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReviewSaveFailuresMapToConflictAndDoNotLeaveHistory(bool expiry)
    {
        var when = Now;
        if (expiry)
        {
            var saved = await service.UpdatePricingAsync("12345678-1", new(order.UpdatedAt, OrderPricingInputs.Empty), actorId, Shift, default);
            await service.ConfirmPricingAsync("12345678-1", new(saved.UpdatedAt), actorId, Shift, default);
            when = Now.AddDays(1);
        }
        var kind = expiry ? OrderHistoryKind.QuoteExpired : OrderHistoryKind.ReviewRejected;
        var fault = new ReviewSaveFailure(kind);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(reviewDatabaseName, reviewDatabaseRoot).AddInterceptors(fault).Options;
        await using var attempted = new AppDbContext(options);
        var subject = new OrderService(attempted, null!, null!, null!, null!, null!, new OffsetClock(when), NullLogger<OrderService>.Instance);
        var error = expiry
            ? await Assert.ThrowsAsync<ServiceException>(() => subject.ExpireQuotesAsync(default))
            : await Assert.ThrowsAsync<ServiceException>(() => subject.RejectReviewAsync("12345678-1", new() { ExpectedUpdatedAt = order.UpdatedAt, Reason = "Причина" }, actorId, Shift, default));
        Assert.That(error!.Code, Is.EqualTo("order_update_conflict"));
        Assert.That(fault.Attempts, Is.EqualTo(expiry ? 3 : 1));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(row => row.Kind == kind), Is.Zero);
        Assert.That((await db.Orders.AsNoTracking().SingleAsync()).Status, Is.EqualTo(expiry ? OrderStatus.QuoteReady : OrderStatus.UnderReview));
    }

    [Test]
    public async Task ExpiryProcessesMoreThanOneBatchWithoutDuplicateEvents()
    {
        var calculation = await OrderPriceCalculator.CalculateAsync(db, order, Now, OrderPricingInputs.Empty, null, default);
        var payload = JsonSerializer.Serialize(calculation, WebJson);
        order.UpdatePricing(Now, true);
        db.Add(new OrderPricingSnapshot { Order = order, At = Now, ValidUntil = Now.AddDays(1), Payload = payload });
        for (var sequence = 2; sequence <= 501; sequence++)
        {
            var ready = new Order(order.CustomerId, sequence, "https://example.com/product", 1, null, Guid.NewGuid(), Now);
            ready.UpdatePricing(Now, true);
            db.Add(ready);
            db.Add(new OrderPricingSnapshot { Order = ready, At = Now, ValidUntil = Now.AddDays(1), Payload = payload });
        }
        await db.SaveChangesAsync();
        await At(Now.AddDays(1)).ExpireQuotesAsync(default);
        Assert.That(await db.Orders.CountAsync(row => row.Status == OrderStatus.QuoteExpired), Is.EqualTo(501));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(row => row.Kind == OrderHistoryKind.QuoteExpired), Is.EqualTo(501));
    }

    private sealed class ReviewSaveFailure(OrderHistoryKind kind) : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<OrderHistoryEvent>().Any(row => row.State == EntityState.Added && row.Entity.Kind == kind))
            {
                Attempts++;
                throw new DbUpdateConcurrencyException();
            }
            return ValueTask.FromResult(result);
        }
    }
}
