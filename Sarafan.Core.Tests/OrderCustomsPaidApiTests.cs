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
    public async Task StaffHistoryCustomsFilterReturnsOnlyPaymentEvidenceAndRejectsUnsupportedAreas()
    {
        var order = await ReadyForCheckout(120);
        var orderPath = "/api/v1/backoffice/orders/" + order.OrderNumber;
        var historyPath = orderPath + "/history";
        var empty = (await _staff.GetFromJsonAsync<OrderHistoryPageDto>(historyPath + "?area=32"))!;
        Assert.That(empty.Area, Is.EqualTo(Sarafan.Core.Models.OrderHistoryArea.Customs));
        Assert.That(empty.Pagination.TotalCount, Is.Zero);
        Assert.That(empty.Items, Is.Empty);

        using var paid = await _staff.PostAsJsonAsync(orderPath + "/customs/paid", new MarkCustomsPaidRequest(order.UpdatedAt));
        paid.EnsureSuccessStatusCode();

        var customs = (await _staff.GetFromJsonAsync<OrderHistoryPageDto>(historyPath + "?area=32"))!;
        Assert.That(customs.Area, Is.EqualTo(Sarafan.Core.Models.OrderHistoryArea.Customs));
        Assert.That(customs.Pagination.TotalCount, Is.EqualTo(1));
        Assert.That(customs.Items, Has.Length.EqualTo(1));
        Assert.That(customs.Items[0].Kind, Is.EqualTo(Sarafan.Core.Models.OrderHistoryKind.CustomsPaid));
        Assert.That(customs.Items[0].Areas, Is.EqualTo(Sarafan.Core.Models.OrderHistoryArea.Customs));
        var pricing = (await _staff.GetFromJsonAsync<OrderHistoryPageDto>(historyPath + "?area=4"))!;
        Assert.That(pricing.Items, Is.Not.Empty);
        Assert.That(pricing.Items.All(item => item.Kind != Sarafan.Core.Models.OrderHistoryKind.CustomsPaid), Is.True);

        using var invalid = await _staff.GetAsync(historyPath + "?area=64");
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await invalid.Content.ReadFromJsonAsync<SarafanProblemDetails>())!.Code, Is.EqualTo("invalid_order_list_filter"));
    }

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
