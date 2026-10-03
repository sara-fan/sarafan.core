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
}
