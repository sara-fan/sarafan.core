// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class OrderPricingTests
{
    [TestCase(OrderStatus.UnderReview)]
    [TestCase(OrderStatus.QuoteReady)]
    [TestCase(OrderStatus.QuoteExpired)]
    public async Task CustomerCanCancelUnpaidOrderOnceAndRetainReasonAndPricing(OrderStatus startingStatus)
    {
        db.Entry(order).Property(item => item.Status).CurrentValue = startingStatus;
        db.OrderPricingSnapshots.Add(new()
        {
            Order = order,
            At = Now,
            Payload = JsonSerializer.Serialize(await OrderPriceCalculator.CalculateAsync(db, order, Now,
                OrderPricingInputs.Empty, null, default), WebJson)
        });
        await db.SaveChangesAsync();
        var before = await service.GetAsync(order.CustomerId, "12345678-1", default);
        Assert.That(before.CanCancel, Is.True);

        var cancelled = await service.CancelAsync(order.CustomerId, before.OrderNumber,
            new CancelOrderRequest { ExpectedUpdatedAt = before.UpdatedAt, Reason = "  Не нужно  " }, default);
        Assert.Multiple(() =>
        {
            Assert.That(cancelled.Status, Is.EqualTo(OrderStatus.Cancelled));
            Assert.That(cancelled.CanCancel, Is.False);
            Assert.That(cancelled.CancelledAt, Is.EqualTo(cancelled.UpdatedAt));
            Assert.That(cancelled.Pricing.TotalRub, Is.EqualTo(before.Pricing.TotalRub));
        });
        var replay = await service.CancelAsync(order.CustomerId, before.OrderNumber,
            new CancelOrderRequest { ExpectedUpdatedAt = before.UpdatedAt, Reason = "Other" }, default);
        Assert.That(replay.CancelledAt, Is.EqualTo(cancelled.CancelledAt));
        Assert.That(await db.OrderPricingSnapshots.CountAsync(), Is.EqualTo(1));
        var events = await db.Set<OrderHistoryEvent>().Where(item => item.Kind == OrderHistoryKind.CustomerCancelled).ToArrayAsync();
        Assert.That(events, Has.Length.EqualTo(1));
        var evidence = JsonSerializer.Deserialize<OrderHistoryEvidence>(events[0].Payload, WebJson)!;
        Assert.That(evidence.StatusBefore, Is.EqualTo(startingStatus));
        Assert.That(evidence.StatusAfter, Is.EqualTo(OrderStatus.Cancelled));
        Assert.That(evidence.CancellationReason, Is.EqualTo("Не нужно"));
        var page = await service.HistoryAsync(before.OrderNumber, Admin, 1, 25, "timestamp", "desc",
            null, null, null, null, null, default);
        var detail = await service.HistoryDetailAsync(before.OrderNumber,
            page.Items.Single(item => item.Kind == OrderHistoryKind.CustomerCancelled).EventKey, Admin, default);
        Assert.That(detail.CancellationReason, Is.EqualTo("Не нужно"));
    }

    [Test]
    public async Task CustomerCancellationAcceptsBlankAndMaximumLengthReasons()
    {
        var blank = await service.CancelAsync(order.CustomerId, "12345678-1",
            new CancelOrderRequest { ExpectedUpdatedAt = order.UpdatedAt, Reason = "   " }, default);
        Assert.That(blank.Status, Is.EqualTo(OrderStatus.Cancelled));
        var second = new Order(order.CustomerId, 2, "https://example.com/product-2", 1,
            null, Guid.NewGuid(), Now);
        second.SetProduct(new("Другой товар", new(10, Currency.Usd), 1, null, null, null));
        db.Orders.Add(second);
        await db.SaveChangesAsync();
        await service.CancelAsync(order.CustomerId, "12345678-2",
            new CancelOrderRequest { ExpectedUpdatedAt = second.UpdatedAt, Reason = new string('x', 2000) }, default);
        var events = await db.Set<OrderHistoryEvent>().OrderBy(item => item.Id).ToArrayAsync();
        var firstEvidence = JsonSerializer.Deserialize<OrderHistoryEvidence>(events[0].Payload, WebJson)!;
        var secondEvidence = JsonSerializer.Deserialize<OrderHistoryEvidence>(events[1].Payload, WebJson)!;
        Assert.That(firstEvidence.CancellationReason, Is.Null);
        Assert.That(secondEvidence.CancellationReason, Has.Length.EqualTo(2000));
    }

    [Test]
    public async Task CustomerCancellationRejectsStaleVersionsInvalidReasonAndOtherCustomers()
    {
        var wrongCustomer = await Assert.ThrowsAsync<ServiceException>(() => service.CancelAsync(order.CustomerId + 1,
            "12345678-1", new CancelOrderRequest { ExpectedUpdatedAt = order.UpdatedAt }, default));
        Assert.That(wrongCustomer!.Code, Is.EqualTo("resource_not_found"));
        var missingVersion = await Assert.ThrowsAsync<ServiceException>(() => service.CancelAsync(order.CustomerId,
            "12345678-1", new CancelOrderRequest(), default));
        Assert.That(missingVersion!.Errors, Does.ContainKey("expectedUpdatedAt"));
        var stale = await Assert.ThrowsAsync<ServiceException>(() => service.CancelAsync(order.CustomerId,
            "12345678-1", new CancelOrderRequest { ExpectedUpdatedAt = order.UpdatedAt.AddSeconds(-1) }, default));
        Assert.That(stale!.Code, Is.EqualTo("order_update_conflict"));
        var invalid = await Assert.ThrowsAsync<ServiceException>(() => service.CancelAsync(order.CustomerId,
            "12345678-1", new CancelOrderRequest { ExpectedUpdatedAt = order.UpdatedAt, Reason = new string('x', 2001) }, default));
        Assert.That(invalid!.Errors, Does.ContainKey("reason"));
        Assert.That(order.Status, Is.EqualTo(OrderStatus.UnderReview));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(), Is.Zero);

        db.Entry(order).Property(item => item.Status).CurrentValue = OrderStatus.Paid;
        await db.SaveChangesAsync();
        var paid = await Assert.ThrowsAsync<ServiceException>(() => service.CancelAsync(order.CustomerId,
            "12345678-1", new CancelOrderRequest { ExpectedUpdatedAt = order.UpdatedAt }, default));
        Assert.That(paid!.Code, Is.EqualTo("order_not_cancellable"));
    }

    [Test]
    public async Task StaffQuoteConfirmationInvalidatesCustomerCancellationVersion()
    {
        var versionBeforeStaffChange = order.UpdatedAt;
        order.UpdatePricing(Now.AddSeconds(1), true);
        await db.SaveChangesAsync();

        var conflict = await Assert.ThrowsAsync<ServiceException>(() => service.CancelAsync(order.CustomerId,
            "12345678-1", new CancelOrderRequest { ExpectedUpdatedAt = versionBeforeStaffChange }, default));
        Assert.That(conflict!.Code, Is.EqualTo("order_update_conflict"));
        Assert.That(order.Status, Is.EqualTo(OrderStatus.QuoteReady));
        Assert.That(await db.Set<OrderHistoryEvent>().AnyAsync(item => item.Kind == OrderHistoryKind.CustomerCancelled),
            Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConcurrentSaveReturnsCurrentCancellationOrConflictWithoutAddingDuplicateHistory(bool competingCancellation)
    {
        var root = new InMemoryDatabaseRoot();
        var name = Guid.NewGuid().ToString();
        var seedOptions = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name, root).Options;
        await using (var seed = new AppDbContext(seedOptions))
        {
            var customer = new Customer { Phone = "+79990001234", CreatedAt = Now, UpdatedAt = Now };
            customer.AllocateOrderNumber("12345678");
            seed.Add(customer);
            await seed.SaveChangesAsync();
            var toCancel = new Order(customer.Id, 1, "https://example.com/product", 1,
                null, Guid.NewGuid(), Now);
            toCancel.SetProduct(new("Товар", new(50, Currency.Usd), 1, null, null, null));
            seed.Add(toCancel);
            await seed.SaveChangesAsync();
        }

        var fault = new RejectCancellationSave(async token =>
        {
            await using var competing = new AppDbContext(seedOptions);
            var current = await competing.Orders.SingleAsync(token);
            if (competingCancellation)
            {
                current.CancelByCustomer(Now.AddSeconds(1));
                competing.Set<OrderHistoryEvent>().Add(new()
                {
                    Order = current,
                    At = current.UpdatedAt,
                    Kind = OrderHistoryKind.CustomerCancelled,
                    Areas = OrderHistoryArea.Status,
                    ActorType = OrderHistoryActor.Customer,
                    ActorId = current.CustomerId,
                    ActorName = "Покупатель",
                    Payload = JsonSerializer.Serialize(new OrderHistoryEvidence(2, OrderStatus.UnderReview,
                        OrderStatus.Cancelled, null), WebJson)
                });
            }
            else
            {
                current.UpdatePricing(Now.AddSeconds(1), false);
            }
            await competing.SaveChangesAsync(token);
        });
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name, root)
            .AddInterceptors(fault).Options;
        await using var attempted = new AppDbContext(options);
        var customerId = await attempted.Customers.Select(item => item.Id).SingleAsync();
        var cancel = new CancelOrderRequest { ExpectedUpdatedAt = Now, Reason = "Attempted cancellation" };
        var subject = new OrderService(attempted, null!, null!, null!, null!, null!, new Clock(),
            NullLogger<OrderService>.Instance);

        if (competingCancellation)
        {
            var result = await subject.CancelAsync(customerId, "12345678-1", cancel, default);
            Assert.That(result.Status, Is.EqualTo(OrderStatus.Cancelled));
            Assert.That(result.CancelledAt, Is.EqualTo(Now.AddSeconds(1)));
        }
        else
        {
            var failure = await Assert.ThrowsAsync<ServiceException>(() =>
                subject.CancelAsync(customerId, "12345678-1", cancel, default));
            Assert.That(failure!.Code, Is.EqualTo("order_update_conflict"));
        }

        await using var verification = new AppDbContext(seedOptions);
        Assert.That(await verification.Set<OrderHistoryEvent>().CountAsync(), Is.EqualTo(competingCancellation ? 1 : 0));
        Assert.That((await verification.Orders.SingleAsync()).Status,
            Is.EqualTo(competingCancellation ? OrderStatus.Cancelled : OrderStatus.UnderReview));
    }

    private sealed class RejectCancellationSave(Func<CancellationToken, Task> concurrentUpdate) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<OrderHistoryEvent>().Any(entry =>
                    entry.State == EntityState.Added && entry.Entity.Kind == OrderHistoryKind.CustomerCancelled))
            {
                await concurrentUpdate(cancellationToken);
                throw new DbUpdateConcurrencyException();
            }
            return result;
        }
    }
}
