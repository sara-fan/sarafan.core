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
    public async Task CheckoutChecksOwnershipVersionExpiryConsentAndDeliveryValidation()
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
        request.ExpectedUpdatedAt = result.UpdatedAt; request.Delivery = "other";
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default)))!.Code, Is.EqualTo("validation_failed"));
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
        var request = CheckoutRequest();
        request.Delivery = "courier";
        request.ExpectedDeliveryAddress = new(null, null, null);
        request.DeliveryAddress = new("123456", "Москва", "Новый адрес");
        var error = await Assert.ThrowsAsync<ServiceException>(() => checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default));
        Assert.That(error!.Code, Is.EqualTo("order_update_conflict"));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(row => row.Kind == OrderHistoryKind.CheckoutSaved), Is.Zero);
        Assert.That((await db.Orders.AsNoTracking().SingleAsync()).CheckoutData, Is.Null);
        Assert.That((await db.Customers.AsNoTracking().SingleAsync()).FirstName, Is.Null);
        Assert.That((await db.Customers.AsNoTracking().SingleAsync()).Address, Is.Null);
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


    [Test]
    public async Task CourierUsesProfileAddressAndPreservesSnapshotAfterProfileEdits()
    {
        var checkout = await CheckoutService();
        order.Customer.PostalCode = "123456"; order.Customer.City = "Москва"; order.Customer.Address = "Улица, 1";
        await db.SaveChangesAsync();
        var request = CheckoutRequest(); request.Delivery = "courier";
        request.ExpectedDeliveryAddress = CustomerDeliveryAddress.From(order.Customer);
        var saved = await checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default);
        Assert.That(saved.Checkout!.Delivery.Destination, Is.EqualTo("123456, Москва, Улица, 1"));
        order.Customer.Address = "Другой адрес"; await db.SaveChangesAsync();
        request.ExpectedUpdatedAt = saved.UpdatedAt; request.ExpectedDeliveryAddress = null;
        var again = await checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default);
        Assert.That(again.Checkout!.Delivery, Is.EqualTo(saved.Checkout.Delivery));
        Assert.That(again.Checkout.Profile.Address, Is.EqualTo("Улица, 1"));
        Assert.That(order.Customer.Address, Is.EqualTo("Другой адрес"));
    }

    [TestCase("postalCode")]
    [TestCase("city")]
    [TestCase("address")]
    [TestCase("stale")]
    [TestCase("missingExpectation")]
    public async Task CourierRejectsMissingOrChangedAddressWithoutWrites(string scenario)
    {
        var checkout = await CheckoutService();
        order.Customer.PostalCode = "123456"; order.Customer.City = "Москва"; order.Customer.Address = "Улица, 1";
        var request = CheckoutRequest(); request.Delivery = "courier";
        request.ExpectedDeliveryAddress = CustomerDeliveryAddress.From(order.Customer);
        if (scenario == "postalCode") order.Customer.PostalCode = null;
        if (scenario == "city") order.Customer.City = " ";
        if (scenario == "address") order.Customer.Address = null;
        if (scenario == "stale") order.Customer.Address = "Другой адрес";
        if (scenario == "missingExpectation") request.ExpectedDeliveryAddress = null;
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<ServiceException>(() => checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default));
        Assert.That(error!.Errors, Does.ContainKey("delivery"));
        Assert.That(order.CheckoutData, Is.Null);
        Assert.That(order.Customer.FirstName, Is.Null);
    }

    [Test]
    public async Task LegacyTestCourierDestinationSurvivesAnotherCheckoutSave()
    {
        var checkout = await CheckoutService();
        var saved = await checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", CheckoutRequest(), default);
        var legacy = saved.Checkout! with { Delivery = new("courier", "Курьерская доставка", "Тестовый адрес: Москва, Тестовая улица, 1") };
        order.SaveCheckout(JsonSerializer.Serialize(legacy, WebJson), Now);
        await db.SaveChangesAsync();
        var request = CheckoutRequest(); request.Delivery = "courier";
        var result = await checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default);
        Assert.That(result.Checkout!.Delivery, Is.EqualTo(legacy.Delivery));
    }

    [Test]
    public async Task DeliveryCanSwitchAndCourierAddressCanBeReplacedBeforePayment()
    {
        var checkout = await CheckoutService();
        var request = CheckoutRequest();
        var pickup = await checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default);
        order.Customer.PostalCode = "123456"; order.Customer.City = "Москва"; order.Customer.Address = "Улица, 1";
        await db.SaveChangesAsync();
        request.ExpectedUpdatedAt = pickup.UpdatedAt; request.Delivery = "courier";
        request.ExpectedDeliveryAddress = CustomerDeliveryAddress.From(order.Customer);
        var courier = await checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default);
        Assert.That(courier.Checkout!.Delivery.Destination, Is.EqualTo("123456, Москва, Улица, 1"));
        order.Customer.Address = "Улица, 2"; await db.SaveChangesAsync();
        request.ExpectedUpdatedAt = courier.UpdatedAt;
        var stale = await Assert.ThrowsAsync<ServiceException>(() => checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default));
        Assert.That(stale!.Errors, Does.ContainKey("delivery"));
        Assert.That((await checkout.GetAsync(order.CustomerId, "12345678-1", default)).Checkout, Is.EqualTo(courier.Checkout));
        request.ExpectedDeliveryAddress = CustomerDeliveryAddress.From(order.Customer);
        var replaced = await checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default);
        Assert.That(replaced.Checkout!.Delivery.Destination, Is.EqualTo("123456, Москва, Улица, 2"));
        Assert.That(replaced.Checkout.Profile.Address, Is.EqualTo("Улица, 2"));
        request.ExpectedUpdatedAt = replaced.UpdatedAt; request.Delivery = "pickup"; request.ExpectedDeliveryAddress = null;
        var again = await checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default);
        Assert.That(again.Checkout!.Delivery.RouteAlias, Is.EqualTo("pickup"));
        Assert.That(again.Pricing.TotalRub, Is.EqualTo(pickup.Pricing.TotalRub));
        var events = await db.Set<OrderHistoryEvent>().Where(row => row.Kind == OrderHistoryKind.CheckoutSaved).OrderBy(row => row.Id).ToArrayAsync();
        Assert.That(events, Has.Length.EqualTo(4));
        var evidence = JsonSerializer.Deserialize<OrderHistoryEvidence>(events[2].Payload, WebJson)!;
        Assert.That(evidence.CheckoutBefore, Is.EqualTo(courier.Checkout));
        Assert.That(evidence.CheckoutAfter, Is.EqualTo(replaced.Checkout));
    }

    [TestCase(OrderStatus.Paid)]
    [TestCase(OrderStatus.PurchasingItem)]
    [TestCase(OrderStatus.Cancelled)]
    public async Task DeliveryChangesAreRejectedAfterPaymentOrCancellation(OrderStatus status)
    {
        var checkout = await CheckoutService();
        var request = CheckoutRequest();
        var saved = await checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default);
        db.Entry(order).Property(row => row.Status).CurrentValue = status;
        await db.SaveChangesAsync();
        request.ExpectedUpdatedAt = saved.UpdatedAt; request.Delivery = "courier";
        var error = await Assert.ThrowsAsync<ServiceException>(() => checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default));
        Assert.That(error!.Code, Is.EqualTo("order_not_editable"));
        Assert.That((await checkout.GetAsync(order.CustomerId, "12345678-1", default)).Checkout, Is.EqualTo(saved.Checkout));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(row => row.Kind == OrderHistoryKind.CheckoutSaved), Is.EqualTo(1));
    }
    [Test]
    public async Task InlineCourierAddressSavesWithRecipientAndSnapshotAndCanReplaceSavedDelivery()
    {
        var checkout = await CheckoutService();
        var request = CheckoutRequest(); request.Delivery = "courier";
        request.ExpectedDeliveryAddress = new("", " ", null);
        request.DeliveryAddress = new(" 654321 ", " Казань ", " Новый адрес ");
        var saved = await checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default);
        Assert.That(saved.Checkout!.Delivery.Destination, Is.EqualTo("654321, Казань, Новый адрес"));
        Assert.That(CustomerDeliveryAddress.From(order.Customer), Is.EqualTo(request.DeliveryAddress.Normalize()));
        Assert.That(order.Customer.FirstName, Is.EqualTo("Иван"));
        Assert.That(saved.Checkout.Profile.Address, Is.EqualTo(order.Customer.Address));
        request.ExpectedUpdatedAt = saved.UpdatedAt;
        request.ExpectedDeliveryAddress = CustomerDeliveryAddress.From(order.Customer);
        request.DeliveryAddress = new("123456", "Москва", "Следующий адрес");
        var replaced = await checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default);
        Assert.That(replaced.Checkout!.Delivery.Destination, Is.EqualTo("123456, Москва, Следующий адрес"));
        Assert.That(order.Customer.Address, Is.EqualTo("Следующий адрес"));
        var events = await db.Set<OrderHistoryEvent>().Where(row => row.Kind == OrderHistoryKind.CheckoutSaved).OrderBy(row => row.Id).ToArrayAsync();
        var evidence = JsonSerializer.Deserialize<OrderHistoryEvidence>(events[1].Payload, WebJson)!;
        Assert.That(evidence.CheckoutBefore, Is.EqualTo(saved.Checkout));
        Assert.That(evidence.CheckoutAfter, Is.EqualTo(replaced.Checkout));
    }

    [TestCase("postalCode", 20)]
    [TestCase("city", 150)]
    [TestCase("address", 500)]
    public async Task InvalidInlineAddressReturnsFieldErrorsWithoutSavingAnything(string field, int maximum)
    {
        var checkout = await CheckoutService();
        var request = CheckoutRequest(); request.Delivery = "courier";
        request.ExpectedDeliveryAddress = CustomerDeliveryAddress.From(order.Customer);
        foreach (var value in new[] { " ", new string('a', maximum + 1) })
        {
            var address = new CustomerDeliveryAddress("123456", "Москва", "Адрес");
            request.DeliveryAddress = field switch
            {
                "postalCode" => address with { PostalCode = value },
                "city" => address with { City = value },
                _ => address with { Address = value }
            };
            var error = await Assert.ThrowsAsync<ServiceException>(() => checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default));
            Assert.That(error!.Errors, Does.ContainKey("deliveryAddress." + field));
            Assert.That(order.CheckoutData, Is.Null);
            Assert.That(order.Customer.Address, Is.Null);
            Assert.That(order.Customer.FirstName, Is.Null);
            Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(row => row.Kind == OrderHistoryKind.CheckoutSaved), Is.Zero);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InlineAddressRequiresUnchangedProfileBeforeSaving(bool omitExpectation)
    {
        var checkout = await CheckoutService();
        var request = CheckoutRequest(); request.Delivery = "courier";
        request.ExpectedDeliveryAddress = omitExpectation ? null : CustomerDeliveryAddress.From(order.Customer);
        request.DeliveryAddress = new("123456", "Москва", "Черновик");
        order.Customer.Address = "Правка в другой вкладке";
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<ServiceException>(() => checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default));
        Assert.That(error!.Errors, Does.ContainKey("delivery"));
        Assert.That(order.Customer.Address, Is.EqualTo("Правка в другой вкладке"));
        Assert.That(order.Customer.FirstName, Is.Null);
        Assert.That(order.CheckoutData, Is.Null);
    }

    [Test]
    public async Task PickupIgnoresInlineCourierDraftWithoutChangingProfileAddress()
    {
        var checkout = await CheckoutService();
        var request = CheckoutRequest();
        request.DeliveryAddress = new("123456", "Москва", "Черновик");
        var saved = await checkout.SaveCheckoutAsync(order.CustomerId, "12345678-1", request, default);
        Assert.That(order.Customer.Address, Is.Null);
        Assert.That(saved.Checkout!.Profile.Address, Is.Null);
        Assert.That(saved.Checkout.Delivery.RouteAlias, Is.EqualTo("pickup"));
    }

}
