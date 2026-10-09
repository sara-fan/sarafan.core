// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Json;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Tests;

public sealed partial class OrderProductApiTests
{
    [Test]
    public async Task StaffDutyPaymentEndpointIsAuthorizedVersionedAndCustomerVisible()
    {
        var order = await ReadyForCheckout(120);
        var path = "/api/v1/backoffice/orders/" + order.OrderNumber + "/customs/paid";
        using var anonymous = IntegrationTestEnvironment.Factory.CreateClient();
        using var unauthorized = await anonymous.PostAsJsonAsync(path, new MarkCustomsPaidRequest(order.UpdatedAt));
        Assert.That(unauthorized.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var customerWrite = await _customer.PostAsJsonAsync(path, new MarkCustomsPaidRequest(order.UpdatedAt));
        Assert.That(customerWrite.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var stale = await _staff.PostAsJsonAsync(path, new MarkCustomsPaidRequest(null));
        Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var paid = await _staff.PostAsJsonAsync(path, new MarkCustomsPaidRequest(order.UpdatedAt));
        paid.EnsureSuccessStatusCode();
        Assert.That(paid.Headers.CacheControl!.NoStore, Is.True);
        var staff = (await paid.Content.ReadFromJsonAsync<BackofficeOrderDetailsDto>())!;
        Assert.That(staff.CustomsPaid, Is.True);
        Assert.That(staff.CanMarkCustomsPaid, Is.False);
        var customer = (await _customer.GetFromJsonAsync<OrderDto>("/api/v1/orders/" + order.OrderNumber))!;
        Assert.That(customer.Pricing.CustomsPaid, Is.True);
        Assert.That(customer.Pricing.TotalRub, Is.EqualTo(order.Pricing.TotalRub));
        using var repeated = await _staff.PostAsJsonAsync(path, new MarkCustomsPaidRequest(staff.UpdatedAt));
        Assert.That((await repeated.Content.ReadFromJsonAsync<SarafanProblemDetails>())!.Code, Is.EqualTo("customs_payment_unavailable"));
    }
}
