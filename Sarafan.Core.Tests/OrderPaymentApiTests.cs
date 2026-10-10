// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class PaymentInformationApiTests
{
    [TestCase(BackofficeRoles.Administrator)]
    [TestCase(BackofficeRoles.ShiftManager)]
    [TestCase(BackofficeRoles.SeniorOperator)]
    [TestCase(BackofficeRoles.Operator)]
    public async Task PaymentHttpContractEnforcesOwnershipRolesNoStoreAndSingleConfirmation(string role)
    {
        var staff = await StaffToken(role); var customer = await CustomerToken();
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = await db.Customers.SingleAsync(); owner.AllocateOrderNumber("12345678");
        var now = DateTimeOffset.UtcNow;
        var order = new Order(owner.Id, 1, "https://example.com/product", 1, null, Guid.NewGuid(), now);
        order.UpdatePricing(now, true);
        order.SaveCheckout(JsonSerializer.Serialize(new OrderCheckoutDto(CustomerProfileDto.From(owner), new("courier", "Курьер", "Адрес")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), now);
        db.Add(order);
        db.Add(new OrderPricingSnapshot
        {
            Order = order,
            At = now,
            ValidUntil = now.AddHours(1),
            Payload = JsonSerializer.Serialize(
            new OrderPriceCalculationDto(now, null,
                [new(ServiceKind.DomesticDelivery, PriceComponentState.Calculated, Currency.Rub, 100, 100, null),
                 new(ServiceKind.CustomsPayments, PriceComponentState.Calculated, Currency.Rub, 120, 120, null)], 1000, OrderPricingInputs.Empty),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
        });
        var other = new Customer { Phone = "+79999999998", CreatedAt = now, UpdatedAt = now };
        db.Add(other); await db.SaveChangesAsync();
        var otherToken = scope.ServiceProvider.GetRequiredService<JwtTokenService>().CreateAccessToken(other).Token;
        const string readPath = "/api/v1/orders/12345678-1/payment";
        const string paidPath = "/api/v1/backoffice/orders/12345678-1/payment/paid";
        foreach (var token in new[] { null, staff })
        { using var denied = await Send(HttpMethod.Get, readPath, token); Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized)); }
        using var forbiddenOwner = await Send(HttpMethod.Get, readPath, otherToken);
        Assert.That(forbiddenOwner.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using var empty = await Send(HttpMethod.Get, readPath, customer);
        var payment = (await empty.Content.ReadFromJsonAsync<OrderPaymentDto>())!;
        Assert.That(empty.Headers.CacheControl!.NoStore, Is.True); Assert.That(payment.CanPay, Is.False);
        Assert.That(payment.MainPaymentRub, Is.EqualTo(1100));
        var information = scope.ServiceProvider.GetRequiredService<PaymentInformationService>();
        var admin = await db.BackofficeUsers.FirstAsync();
        var bundle = await information.CreateAsync(PaymentInformationTests.Complete(), admin.Id, PaymentInformationTests.Admin, default);
        await information.EnableAsync(bundle.Id, new(bundle.Version, null), admin.Id, PaymentInformationTests.Admin, default);
        using var available = await Send(HttpMethod.Get, readPath, customer);
        Assert.That((await available.Content.ReadFromJsonAsync<OrderPaymentDto>())!.CanPay, Is.True);
        foreach (var token in new[] { null, customer })
        { using var denied = await Send(HttpMethod.Post, paidPath, token, JsonContent.Create(new { expectedUpdatedAt = payment.Order.UpdatedAt })); Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized)); }
        using var paid = await Send(HttpMethod.Post, paidPath, staff, JsonContent.Create(new { expectedUpdatedAt = payment.Order.UpdatedAt }));
        Assert.That(paid.StatusCode, Is.EqualTo(HttpStatusCode.OK), await paid.Content.ReadAsStringAsync());
        var result = (await paid.Content.ReadFromJsonAsync<BackofficeOrderDetailsDto>())!;
        Assert.That(result.Status, Is.EqualTo(OrderStatus.Paid)); Assert.That(result.CanMarkOrderPaid, Is.False);
        using var repeat = await Send(HttpMethod.Post, paidPath, staff, JsonContent.Create(new { expectedUpdatedAt = result.UpdatedAt }));
        await Problem(repeat, HttpStatusCode.Conflict, "order_payment_unavailable");
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(row => row.Kind == OrderHistoryKind.OrderPaid), Is.EqualTo(1));
    }
}
