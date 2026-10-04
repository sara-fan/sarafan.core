// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class OrderProductApiTests
{
    private async Task<OrderDto> ReadyForCheckout()
    {
        using var response = await Create(Guid.NewGuid()); response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<OrderDto>())!;
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Orders.SingleAsync();
        var now = DateTimeOffset.UtcNow;
        foreach (var kind in new[] { ServiceKind.UsWarehouseExpenses, ServiceKind.InternationalDelivery, ServiceKind.ServiceCommission, ServiceKind.DomesticDelivery })
            db.ServiceCatalogueEntries.Add(new(kind, PriceMethod.Fixed, null, null, null, 10m, Currency.Rub, null, null, now));
        await db.SaveChangesAsync();
        var calculation = await OrderPriceCalculator.CalculateAsync(db, row, now, OrderPricingInputs.Empty, null, default);
        row.UpdatePricing(now, true);
        db.OrderPricingSnapshots.Add(new() { Order = row, At = now, ValidUntil = now.AddHours(1), Payload = JsonSerializer.Serialize(calculation, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        await db.SaveChangesAsync();
        return (await _customer.GetFromJsonAsync<OrderDto>("/api/v1/orders/" + result.OrderNumber))!;
    }
    [Test]
    public async Task CheckoutEndpointPersistsCustomerDetailsAndRetainsOrderSnapshot()
    {
        var order = await ReadyForCheckout();
        var path = "/api/v1/orders/" + order.OrderNumber + "/checkout";
        var request = new OrderCheckoutRequest
        {
            ExpectedUpdatedAt = order.UpdatedAt,
            Delivery = "courier",
            Profile = new() { FirstName = "Пётр", LastName = "Петров", Email = null, Inn = "123456789012" }
        };
        using var anonymous = IntegrationTestEnvironment.Factory.CreateClient();
        using var unauthorized = await anonymous.PostAsJsonAsync(path, request);
        Assert.That(unauthorized.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        var address = new CustomerDeliveryAddress(" 123456 ", " Москва ", " Улица, 1 ");
        using var addressResponse = await _customer.PutAsJsonAsync("/api/v1/customers/me/delivery-address", address);
        addressResponse.EnsureSuccessStatusCode();
        var addressCustomer = (await addressResponse.Content.ReadFromJsonAsync<CustomerDto>())!;
        Assert.That(addressCustomer.Profile.PostalCode, Is.EqualTo("123456"));
        Assert.That(addressCustomer.Profile.FirstName, Is.EqualTo(_session.Customer.Profile.FirstName));
        request.ExpectedDeliveryAddress = address.Normalize();
        using var savedResponse = await _customer.PostAsJsonAsync(path, request); savedResponse.EnsureSuccessStatusCode();
        var body = await savedResponse.Content.ReadAsStringAsync();
        Assert.That(body, Does.Not.Contain("recipientPhone").And.Not.Contain("birthDate").And.Not.Contain("passportDepartmentCode"));
        var saved = (await savedResponse.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.That(saved.Checkout!.Profile.Phone, Is.EqualTo(_session.Customer.Phone));
        Assert.That(saved.Pricing.TotalRub, Is.EqualTo(order.Pricing.TotalRub));
        Assert.That(saved.Pricing.DomesticDeliveryRub, Is.EqualTo(10));
        Assert.That(savedResponse.Headers.CacheControl!.NoStore, Is.True);
        var customer = (await _customer.GetFromJsonAsync<CustomerDto>("/api/v1/customers/me"))!;
        Assert.That(customer.Phone, Is.EqualTo(_session.Customer.Phone));
        Assert.That(customer.Profile.FirstName, Is.EqualTo("Пётр"));
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persisted = await database.Customers.AsNoTracking().SingleAsync(row => row.Id == customer.Id);
            Assert.That(persisted.FirstName, Is.EqualTo("Пётр"));
            Assert.That(persisted.Inn, Is.EqualTo("123456789012"));
        }
        using var oldProfileUpdate = await _customer.PutAsJsonAsync("/api/v1/customers/me", new CustomerProfileUpdateRequest { FirstName = "Новый", LastName = "Петров" });
        oldProfileUpdate.EnsureSuccessStatusCode();
        var updated = (await oldProfileUpdate.Content.ReadFromJsonAsync<CustomerDto>())!;
        Assert.That(updated.Phone, Is.EqualTo(_session.Customer.Phone));
        Assert.That(updated.Profile.FirstName, Is.EqualTo("Новый"));
        Assert.That((await _customer.GetFromJsonAsync<OrderDto>("/api/v1/orders/" + order.OrderNumber))!.Checkout!.Profile.FirstName, Is.EqualTo("Пётр"));
        using var stale = await _customer.PostAsJsonAsync(path, request);
        await Problem(stale, HttpStatusCode.Conflict, "order_update_conflict");
    }
    [Test]
    public async Task CheckoutReturnsStructuredValidationWithoutProfileOrOrderWrites()
    {
        var order = await ReadyForCheckout();
        var path = "/api/v1/orders/" + order.OrderNumber + "/checkout";
        using var response = await _customer.PostAsJsonAsync(path, new OrderCheckoutRequest { ExpectedUpdatedAt = order.UpdatedAt, Profile = new() });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var error = (await response.Content.ReadFromJsonAsync<SarafanProblemDetails>())!;
        Assert.That(error.Code, Is.EqualTo("validation_failed"));
        Assert.That(error.Errors, Does.ContainKey("profile.firstName"));
        Assert.That(error.Errors, Does.ContainKey("delivery"));
        Assert.That((await _customer.GetFromJsonAsync<OrderDto>("/api/v1/orders/" + order.OrderNumber))!.Checkout, Is.Null);
        using var unknown = await _customer.PostAsJsonAsync("/api/v1/orders/00000000-1/checkout", new OrderCheckoutRequest { Profile = new() });
        await Problem(unknown, HttpStatusCode.NotFound, "resource_not_found");
    }

    [TestCase(null, "Москва", "Улица", "postalCode")]
    [TestCase("123456", " ", "Улица", "city")]
    [TestCase("123456", "Москва", null, "address")]
    public async Task DeliveryAddressEndpointRequiresAllFields(string? postal, string? city, string? address, string field)
    {
        using var response = await _customer.PutAsJsonAsync("/api/v1/customers/me/delivery-address", new CustomerDeliveryAddress(postal, city, address));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var error = (await response.Content.ReadFromJsonAsync<SarafanProblemDetails>())!;
        Assert.That(error.Errors, Does.ContainKey(field));
        Assert.That((await _customer.GetFromJsonAsync<CustomerDto>("/api/v1/customers/me"))!.Profile.Address, Is.Null);
    }
    [Test]
    public async Task DeliveryAddressEndpointRequiresAuthentication()
    {
        using var anonymous = IntegrationTestEnvironment.Factory.CreateClient();
        using var response = await anonymous.PutAsJsonAsync("/api/v1/customers/me/delivery-address", new CustomerDeliveryAddress("123456", "Москва", "Улица"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task DeliveryAddressRequiresCurrentConsentAndPreservesUnrelatedProfileData()
    {
        var before = new CustomerProfileUpdateRequest { FirstName = "Иван", LastName = "Иванов", Email = "test@example.com", PassportNumber = "123456", Inn = "123456789012" };
        using var profileResponse = await _customer.PutAsJsonAsync("/api/v1/customers/me", before);
        profileResponse.EnsureSuccessStatusCode();
        var original = (await profileResponse.Content.ReadFromJsonAsync<CustomerDto>())!;
        var address = new CustomerDeliveryAddress("123456", "Москва", "Улица");
        using var savedResponse = await _customer.PutAsJsonAsync("/api/v1/customers/me/delivery-address", address);
        savedResponse.EnsureSuccessStatusCode();
        var saved = (await savedResponse.Content.ReadFromJsonAsync<CustomerDto>())!;
        Assert.That(saved.Profile, Is.EqualTo(original.Profile with { PostalCode = address.PostalCode, City = address.City, Address = address.Address }));
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var consent = await db.ConsentEvents.Where(row => row.CustomerId == original.Id && row.Kind == LegalDocumentKind.PersonalDataConsent).OrderByDescending(row => row.Id).FirstAsync();
            consent.Decision = "withdraw";
            await db.SaveChangesAsync();
        }
        using var rejected = await _customer.PutAsJsonAsync("/api/v1/customers/me/delivery-address", address with { Address = "Новый адрес" });
        await Problem(rejected, HttpStatusCode.Conflict, "personal_data_consent_required");
        Assert.That((await _customer.GetFromJsonAsync<CustomerDto>("/api/v1/customers/me"))!.Profile, Is.EqualTo(saved.Profile));
    }

    [TestCase("postalCode", 20)]
    [TestCase("city", 150)]
    [TestCase("address", 500)]
    public async Task DeliveryAddressEnforcesExistingLengthLimits(string field, int maximum)
    {
        var address = new CustomerDeliveryAddress("123456", "Москва", "Улица");
        address = field switch
        {
            "postalCode" => address with { PostalCode = new string('1', maximum + 1) },
            "city" => address with { City = new string('а', maximum + 1) },
            _ => address with { Address = new string('а', maximum + 1) }
        };
        using var response = await _customer.PutAsJsonAsync("/api/v1/customers/me/delivery-address", address);
        var error = (await response.Content.ReadFromJsonAsync<SarafanProblemDetails>())!;
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(error.Errors, Does.ContainKey(field));
    }
    [Test]
    public async Task CheckoutEndpointSavesInlineAddressAndReturnsAddressFieldValidation()
    {
        var order = await ReadyForCheckout();
        var customer = (await _customer.GetFromJsonAsync<CustomerDto>("/api/v1/customers/me"))!;
        var request = new OrderCheckoutRequest
        {
            ExpectedUpdatedAt = order.UpdatedAt,
            Delivery = "courier",
            ExpectedDeliveryAddress = new(customer.Profile.PostalCode, customer.Profile.City, customer.Profile.Address),
            DeliveryAddress = new("", "Казань", "Новый адрес"),
            Profile = new() { FirstName = "Пётр", LastName = "Петров" }
        };
        var path = "/api/v1/orders/" + order.OrderNumber + "/checkout";
        using var invalid = await _customer.PostAsJsonAsync(path, request);
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = (await invalid.Content.ReadFromJsonAsync<SarafanProblemDetails>())!;
        Assert.That(problem.Errors, Does.ContainKey("deliveryAddress.postalCode"));
        Assert.That((await _customer.GetFromJsonAsync<CustomerDto>("/api/v1/customers/me"))!.Profile, Is.EqualTo(customer.Profile));
        request.DeliveryAddress = request.DeliveryAddress with { PostalCode = "654321" };
        using var response = await _customer.PostAsJsonAsync(path, request);
        response.EnsureSuccessStatusCode();
        var saved = (await response.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.That(saved.Checkout!.Delivery.Destination, Is.EqualTo("654321, Казань, Новый адрес"));
        var updated = (await _customer.GetFromJsonAsync<CustomerDto>("/api/v1/customers/me"))!;
        Assert.That(updated.Profile.Address, Is.EqualTo("Новый адрес"));
        Assert.That(updated.Profile.FirstName, Is.EqualTo("Пётр"));
    }

    [TestCase("courier")]
    [TestCase("pickup")]
    public async Task StaffOrderDeliveryUsesCheckoutSnapshotInsteadOfCurrentProfileAddress(string routeAlias)
    {
        var order = await ReadyForCheckout();
        var firstAddress = new CustomerDeliveryAddress("123456", "Москва", "Адрес при оформлении");
        using var profileResponse = await _customer.PutAsJsonAsync("/api/v1/customers/me/delivery-address", firstAddress);
        profileResponse.EnsureSuccessStatusCode();
        var unselected = await Details(order.OrderNumber);
        Assert.That(unselected.Delivery, Is.Null);
        var request = new OrderCheckoutRequest
        {
            ExpectedUpdatedAt = order.UpdatedAt,
            Delivery = routeAlias,
            ExpectedDeliveryAddress = routeAlias == "courier" ? firstAddress : null,
            Profile = new() { FirstName = "Иван", LastName = "Иванов", PassportNumber = "123456", PassportIssueDate = new(2010, 2, 3) }
        };
        using var checkoutResponse = await _customer.PostAsJsonAsync("/api/v1/orders/" + order.OrderNumber + "/checkout", request);
        checkoutResponse.EnsureSuccessStatusCode();
        var saved = (await checkoutResponse.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.That((await Details(order.OrderNumber)).Delivery, Is.EqualTo(saved.Checkout!.Delivery));
        using var changedProfile = await _customer.PutAsJsonAsync("/api/v1/customers/me/delivery-address",
            new CustomerDeliveryAddress("654321", "Казань", "Более поздний адрес профиля"));
        changedProfile.EnsureSuccessStatusCode();
        using var staffResponse = await _staff.GetAsync("/api/v1/backoffice/orders/" + order.OrderNumber);
        staffResponse.EnsureSuccessStatusCode();
        Assert.That(staffResponse.Headers.CacheControl!.NoStore, Is.True);
        var staff = (await staffResponse.Content.ReadFromJsonAsync<BackofficeOrderDetailsDto>())!;
        Assert.That(staff.Delivery, Is.EqualTo(saved.Checkout.Delivery));
        Assert.That(staff.Customer.PassportNumber, Is.EqualTo("123456"));
        Assert.That(staff.Customer.PassportIssueDate, Is.EqualTo(new DateOnly(2010, 2, 3)));
        using var json = JsonDocument.Parse(await staffResponse.Content.ReadAsStringAsync());
        var customer = json.RootElement.GetProperty("customer");
        Assert.That(customer.TryGetProperty("postalCode", out _), Is.False);
        Assert.That(customer.TryGetProperty("city", out _), Is.False);
        Assert.That(customer.TryGetProperty("address", out _), Is.False);
        Assert.That(json.RootElement.TryGetProperty("checkout", out _), Is.False);
        Assert.That(json.RootElement.GetProperty("delivery").GetProperty("destination").GetString(), Is.EqualTo(saved.Checkout.Delivery.Destination));
    }

    [Test]
    public async Task StaffOrderDeliveryPreservesHistoricalTestCourierDestination()
    {
        var order = await ReadyForCheckout();
        var legacy = new OrderCheckoutDto(_session.Customer.Profile,
            new("courier", "Курьерская доставка", "Тестовый адрес: Москва, Тестовая улица, 1"));
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await database.Orders.SingleAsync();
            row.SaveCheckout(JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)), DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();
        }
        Assert.That((await Details(order.OrderNumber)).Delivery, Is.EqualTo(legacy.Delivery));
    }

}
