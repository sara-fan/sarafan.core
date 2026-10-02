// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sarafan.Core.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sarafan.Core.Authentication;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class OrderPricingTests
{
    private async Task<OrderService> CheckoutService(DateTimeOffset? at = null)
    {
        foreach (var kind in new[] { LegalDocumentKind.PersonalDataConsent, LegalDocumentKind.UserAgreement })
        {
            var document = new LegalDocument { Kind = kind, EffectiveAt = Now.AddDays(-1), CreatedAt = Now.AddDays(-1), CreatedBy = actorId, ContentHash = new string('a', 64) };
            db.Add(document);
            db.Add(new ConsentEvent { CustomerId = order.CustomerId, Document = document, Kind = kind, Decision = "grant", ContentHash = document.ContentHash, At = Now.AddDays(-1), RetainUntil = Now.AddYears(1), IdempotencyKey = Guid.NewGuid() });
        }
        db.Add(Tariff(ServiceKind.DomesticDelivery, PriceMethod.Fixed, Currency.Rub, amount: 55));
        await db.SaveChangesAsync();
        var saved = await service.UpdatePricingAsync("12345678-1", new(order.UpdatedAt, OrderPricingInputs.Empty), actorId, Shift, default);
        await service.ConfirmPricingAsync("12345678-1", new(saved.UpdatedAt), actorId, Shift, default);
        var clock = new OffsetClock(at ?? Now);
        var consents = new ConsentService(db, clock, Options.Create(new ConsentOptions()), Options.Create(new AuthenticationOptions()), NullLogger<ConsentService>.Instance);
        return new(db, consents, null!, null!, null!, null!, clock, NullLogger<OrderService>.Instance);
    }
    private OrderCheckoutRequest CheckoutRequest() => new()
    {
        ExpectedUpdatedAt = order.UpdatedAt,
        Delivery = "pickup",
        Profile = new()
        {
            FirstName = " Иван ",
            LastName = " Иванов ",
            Email = " TEST@example.com ",
            Patronymic = " Иванович ",
            PassportSeries = " 1234 ",
            PassportNumber = " 123456 ",
            PassportIssueDate = new(2010, 1, 1),
            PassportIssuedBy = " МВД ",
            Inn = " 123456789012 "
        }
    };
    [Test]
    public async Task CheckoutAtomicallySavesCustomerDetailsAndOrderDeliveryWithoutChangingQuote()
    {
        var checkout = await CheckoutService();
        order.Customer.City = "Москва"; order.Customer.Address = "Старый адрес";
        await db.SaveChangesAsync();
        var before = await service.GetAsync(order.CustomerId, "12345678-1", default);
        var snapshot = (await db.OrderPricingSnapshots.OrderByDescending(row => row.Id).FirstAsync()).Payload;
        var result = await checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", CheckoutRequest(), default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Checkout!.Profile.Phone, Is.EqualTo("+79990001234"));
            Assert.That(result.Checkout!.Profile.FirstName, Is.EqualTo("Иван"));
            Assert.That(result.Checkout!.Profile.Email, Is.EqualTo("test@example.com"));
            Assert.That(result.Checkout.Delivery.RouteAlias, Is.EqualTo("pickup"));
            Assert.That(result.Checkout.Delivery.Destination, Does.Contain("Тестовый ПВЗ"));
            Assert.That(result.Pricing.TotalRub, Is.EqualTo(before.Pricing.TotalRub));
            Assert.That(result.Pricing.DomesticDeliveryRub, Is.EqualTo(55));
            Assert.That(result.UpdatedAt, Is.GreaterThan(before.UpdatedAt));
            Assert.That(order.Customer.Phone, Is.EqualTo("+79990001234"));
            Assert.That(order.Customer.Address, Is.EqualTo("Старый адрес"));
        });
        Assert.That((await checkout.ListAsync(order.CustomerId, default)).Single().Pricing.DomesticDeliveryRub, Is.EqualTo(55));
        Assert.That((await db.OrderPricingSnapshots.OrderByDescending(row => row.Id).FirstAsync()).Payload, Is.EqualTo(snapshot));
        db.ChangeTracker.Clear();
        var profile = await db.Customers.SingleAsync(row => row.Id == order.CustomerId);
        Assert.That(profile.FirstName, Is.EqualTo("Иван"));
        Assert.That(profile.PassportNumber, Is.EqualTo("123456"));
        Assert.That(profile.Phone, Is.EqualTo("+79990001234"));
        Assert.That((await checkout.GetAsync(profile.Id, "12345678-1", default)).Checkout, Is.EqualTo(result.Checkout));
    }
    [TestCase("firstName")]
    [TestCase("lastName")]
    [TestCase("delivery")]
    [TestCase("email")]
    [TestCase("inn")]
    [TestCase("passportIssueDate")]
    [TestCase("length")]
    [TestCase("profile")]
    public async Task CheckoutRejectsInvalidFieldsWithoutSaving(string field)
    {
        var checkout = await CheckoutService(); var request = CheckoutRequest();
        switch (field)
        {
            case "firstName": request.Profile!.FirstName = " "; break;
            case "lastName": request.Profile!.LastName = null; break;
            case "delivery": request.Delivery = "other"; break;
            case "email": request.Profile!.Email = "invalid"; break;
            case "inn": request.Profile!.Inn = "123"; break;
            case "passportIssueDate": request.Profile!.PassportIssueDate = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(1); break;
            case "length": request.Profile!.FirstName = new string('a', 101); field = "firstName"; break;
            case "profile": request.Profile = null; field = "firstName"; break;
        }
        var error = await Assert.ThrowsAsync<ServiceException>(() => checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default));
        Assert.That(error!.Code, Is.EqualTo("validation_failed"));
        Assert.That(error.Errors, Does.ContainKey(new[] { "firstName", "lastName", "email", "inn", "passportIssueDate" }.Contains(field) ? "profile." + field : field));
        Assert.That(order.CheckoutData, Is.Null); Assert.That(order.Customer.FirstName, Is.Null);
    }
    [Test]
    public async Task CheckoutChecksOwnershipVersionExpiryConsentAndImmutableDelivery()
    {
        var checkout = await CheckoutService(); var request = CheckoutRequest();
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => checkout.SaveCheckoutAsync(999, "12345678-1", request, default)))!.StatusCode, Is.EqualTo(404));
        request.ExpectedUpdatedAt = null;
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default)))!.Code, Is.EqualTo("order_update_conflict"));
        request.ExpectedUpdatedAt = order.UpdatedAt;
        foreach (var kind in new[] { LegalDocumentKind.PersonalDataConsent, LegalDocumentKind.UserAgreement })
        {
            var grant = await db.ConsentEvents.SingleAsync(row => row.Kind == kind); grant.Decision = "withdraw"; await db.SaveChangesAsync();
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default)))!.Code,
                Is.EqualTo(kind == LegalDocumentKind.UserAgreement ? "user_agreement_required" : "personal_data_consent_required"));
            Assert.That(order.CheckoutData, Is.Null); grant.Decision = "grant"; await db.SaveChangesAsync();
        }
        request.Profile!.Email = null; request.Profile.Patronymic = null;
        var result = await checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default);
        Assert.That(result.Checkout!.Profile.Email, Is.Null);
        request.Profile.FirstName = "Пётр";
        request.ExpectedUpdatedAt = result.UpdatedAt; request.Delivery = "courier";
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default)))!.Code, Is.EqualTo("order_not_editable"));
        request.Delivery = "pickup";
        var changed = await checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default);
        var events = await db.Set<OrderHistoryEvent>().Where(row => row.Kind == OrderHistoryKind.CheckoutSaved).OrderBy(row => row.Id).ToArrayAsync();
        Assert.That(events, Has.Length.EqualTo(2));
        var evidence = JsonSerializer.Deserialize<OrderHistoryEvidence>(events[1].Payload, WebJson)!;
        Assert.That(evidence.CheckoutBefore, Is.EqualTo(result.Checkout));
        Assert.That(evidence.CheckoutAfter, Is.EqualTo(changed.Checkout));
        Assert.That(events[1].At, Is.EqualTo(changed.UpdatedAt));
        Assert.That(events[1].ActorType, Is.EqualTo(OrderHistoryActor.Customer));
        var detail = await checkout.HistoryDetailAsync("12345678-1", $"0-{events[1].Id}", Shift, default);
        Assert.That(detail.Version, Is.EqualTo(4));
        Assert.That(detail.CheckoutDeliveryName, Is.EqualTo(changed.Checkout!.Delivery.Name));
        Assert.That(JsonSerializer.Serialize(detail, WebJson), Does.Not.Contain(changed.Checkout.Profile.Phone));
        Assert.That((await checkout.HistoryAsync("12345678-1", Shift, 1, 25, "timestamp", "desc", null, 16, null, null, null, default)).Items, Has.Length.EqualTo(2));
        Assert.That((await checkout.HistoryAsync("12345678-1", Shift, 1, 25, "timestamp", "desc", "Получатель и доставка", null, null, null, null, default)).Items, Has.Length.EqualTo(2));
        var expired = new OrderService(db, null!, null!, null!, null!, null!, new OffsetClock(Now.AddDays(2)), NullLogger<OrderService>.Instance);
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => expired.SaveCheckoutAsync(999, "12345678-1", request, default)))!.StatusCode, Is.EqualTo(404));
        await expired.ExpireQuotesAsync(default);
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default)))!.Code, Is.EqualTo("order_not_editable"));
    }
    [Test]
    public async Task CheckoutConcurrencyFailureLeavesProfileSnapshotAndHistoryUnchanged()
    {
        await CheckoutService();
        await using var failing = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(reviewDatabaseName, reviewDatabaseRoot).AddInterceptors(new CheckoutSaveFailure()).Options);
        var clock = new Clock();
        var consents = new ConsentService(failing, clock, Options.Create(new ConsentOptions()), Options.Create(new AuthenticationOptions()), NullLogger<ConsentService>.Instance);
        var checkout = new OrderService(failing, consents, null!, null!, null!, null!, clock, NullLogger<OrderService>.Instance);
        var error = await Assert.ThrowsAsync<ServiceException>(() => checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", CheckoutRequest(), default));
        Assert.That(error!.Code, Is.EqualTo("order_update_conflict"));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(row => row.Kind == OrderHistoryKind.CheckoutSaved), Is.Zero);
        Assert.That((await db.Orders.AsNoTracking().SingleAsync()).CheckoutData, Is.Null);
        Assert.That((await db.Customers.AsNoTracking().SingleAsync()).FirstName, Is.Null);
        Assert.That(failing.ChangeTracker.Entries(), Is.Empty);
    }
    private sealed class CheckoutSaveFailure : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries<OrderHistoryEvent>().Any(row => row.State == EntityState.Added && row.Entity.Kind == OrderHistoryKind.CheckoutSaved))
                throw new DbUpdateConcurrencyException();
            return ValueTask.FromResult(result);
        }
    }

}
