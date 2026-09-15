// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed class OrderProductRulesTests
{
    private static OrderProductDto Product => new("Товар", new(10, Currency.Usd), 1, null, null, null);

    [TestCase(true)]
    [TestCase(false)]
    public void RecognitionFixturesSupportCompleteAndPartialPrefillWithoutRelabellingCurrency(bool complete)
    {
        var fixture = new ProductPreviewDto("https://shop.example.com/", ProductPreviewDto.RecognizedOutcome)
        {
            Product = new(complete ? "Товар" : null, complete ? new(40, Currency.Eur) : null, 1, "Cherry", null, null)
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var decoded = JsonSerializer.Deserialize<ProductPreviewDto>(JsonSerializer.Serialize(fixture, options), options)!;
        Assert.That(decoded, Is.EqualTo(fixture));
        Assert.That(decoded.Product!.SellerPrice?.Currency, Is.EqualTo(complete ? Currency.Eur : (Currency?)null));
    }

    [Test]
    public void NormalizationPreservesInternalTextAndCase()
    {
        var result = OrderProductRules.Normalize(new()
        {
            ProductName = "  Stanley  H2.0  ",
            SellerPrice = new(40, Currency.Usd),
            Color = " Cherry Blossom ",
            Size = "  "
        }, 4, "  Тест  заказа  ");
        Assert.That(result, Is.EqualTo(new OrderProductDto("Stanley  H2.0", new(40, Currency.Usd), 4, "Cherry Blossom", null, "Тест  заказа")));
        Assert.DoesNotThrow(() => OrderProductRules.Validate(result));
        Assert.That(OrderProductRules.Normalize(null, 1, null).ProductName, Is.Null);
    }

    [TestCase(1)]
    [TestCase(4)]
    public void QuantityBoundariesAreValid(int quantity) => Assert.DoesNotThrow(() => OrderProductRules.Validate(Product with { Quantity = quantity }));

    [TestCase(0, "invalid_order_quantity")]
    [TestCase(-1, "invalid_order_quantity")]
    [TestCase(5, "order_quantity_limit_exceeded")]
    [TestCase(int.MaxValue, "order_quantity_limit_exceeded")]
    public void QuantityRejectsInvalidValues(int quantity, string code) => Reject(Product with { Quantity = quantity }, code);

    [Test]
    public void RejectsMissingMoneyWrongCurrencyAndEveryTextBoundary()
    {
        Reject(Product with { ProductName = null }, "invalid_order_product_name");
        Reject(Product with { ProductName = new string('я', 501) }, "invalid_order_product_name");
        Reject(Product with { SellerPrice = null }, "invalid_order_seller_price");
        foreach (var amount in new[] { 0m, -1m, 0.001m, 100000000m, decimal.MaxValue })
            Reject(Product with { SellerPrice = new(amount, Currency.Usd) }, "invalid_order_seller_price");
        foreach (var currency in new[] { Currency.Rub, Currency.Eur, (Currency)999 })
            Reject(Product with { SellerPrice = new(10m, currency) }, "invalid_order_seller_price");
        Reject(Product with { Color = new string('я', 201) }, "invalid_order_color");
        Reject(Product with { Size = new string('я', 201) }, "invalid_order_size");
        Reject(Product with { Comment = new string('я', 2001) }, "invalid_order_comment");
        Assert.DoesNotThrow(() => OrderProductRules.Validate(Product with
        {
            ProductName = new string('я', 500),
            SellerPrice = new(99999999.99m, Currency.Usd),
            Color = new string('я', 200),
            Size = new string('я', 200),
            Comment = new string('я', 2000)
        }));
    }

    [Test]
    public void LimitIsInclusiveAndDoesNotRoundAwayExcessOrIgnoreNominals()
    {
        var pair = new OrderLimitRatePair(OrderProductTestData.Rate(Currency.Usd, 8000, 100),
            OrderProductTestData.Rate(Currency.Eur, 1000, 10));
        Assert.That(pair.MaximumTotalUsd, Is.EqualTo(1125m));
        Assert.That(pair.Allows(281.25m, 4), Is.True);
        Assert.That(pair.Allows(281.26m, 4), Is.False);
        Assert.That(pair.Allows(1124.99m, 1), Is.True);
        // 900.000009 EUR would round to 900.00, but must be rejected.
        var boundary = new OrderLimitRatePair(OrderProductTestData.Rate(Currency.Usd, 100.000001m),
            OrderProductTestData.Rate(Currency.Eur, 100m));
        Assert.That(boundary.Allows(900m, 1), Is.False);
        Assert.That(boundary.MaximumTotalUsd, Is.EqualTo(899.99m));
        var extreme = new OrderLimitRatePair(OrderProductTestData.Rate(Currency.Usd, 0.000001m, 1000000),
            OrderProductTestData.Rate(Currency.Eur, 999999999999.999999m));
        Assert.That(extreme.MaximumTotalUsd, Is.EqualTo(399999999.96m));
        Assert.That(extreme.Allows(99999999.99m, 4), Is.True);
        Assert.That(OrderLimitService.Validate(Product, pair), Is.SameAs(pair));
        Assert.That(Assert.Throws<ServiceException>(() => OrderLimitService.Validate(Product, null))!.Code, Is.EqualTo("order_limit_rates_unavailable"));
        Assert.That(Assert.Throws<ServiceException>(() => OrderLimitService.Validate(Product with { SellerPrice = new(1125.01m, Currency.Usd) }, pair))!.Code, Is.EqualTo("order_value_limit_exceeded"));
    }

    [Test]
    public async Task CommonPairUsesLatestSharedDateBeforeMoscowToday()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var time = new FixedTime(DateTimeOffset.Parse("2026-09-14T21:00:00Z"));
        var service = new OrderLimitService(db, time, NullLogger<OrderLimitService>.Instance);
        Assert.That(await service.GetPairAsync(default), Is.Null);
        Assert.That(OrderLimitService.Limits(null).ValueLimit.Available, Is.False);
        db.ExchangeRateHistory.AddRange(
            OrderProductTestData.Rate(Currency.Usd, 80, date: new(2026, 9, 12)),
            OrderProductTestData.Rate(Currency.Eur, 100, date: new(2026, 9, 12)),
            OrderProductTestData.Rate(Currency.Usd, 90, date: new(2026, 9, 15)),
            OrderProductTestData.Rate(Currency.Eur, 100, date: new(2026, 9, 16)));
        await db.SaveChangesAsync();
        var pair = await service.GetPairAsync(default);
        Assert.That(pair!.Usd.SourceEffectiveDate, Is.EqualTo(new DateOnly(2026, 9, 12)));
        db.ExchangeRateHistory.Add(OrderProductTestData.Rate(Currency.Eur, 120, date: new(2026, 9, 15)));
        await db.SaveChangesAsync();
        pair = await service.GetPairAsync(default);
        var metadata = OrderLimitService.Limits(pair);
        Assert.That(metadata.ValueLimit.SourceEffectiveDate, Is.EqualTo(new DateOnly(2026, 9, 15)));
        Assert.That(metadata.ValueLimit.MaximumTotalUsd, Is.EqualTo(1200m));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.CatchAsync<OperationCanceledException>(() => service.GetPairAsync(cancelled.Token));
    }

    [Test]
    public void ExactRussianErrorsAreCentralizedAndFieldStructured()
    {
        var factory = new SarafanProblemDetailsFactory();
        var quantity = factory.Create(new DefaultHttpContext(), 400, "order_quantity_limit_exceeded");
        Assert.That(quantity.Detail, Is.EqualTo("Такое количество товара может быть признано коммерческой партией и запрещено к ввозу"));
        Assert.That(quantity.Errors!["quantity"], Is.EqualTo(new[] { quantity.Detail }));
        var price = factory.Create(new DefaultHttpContext(), 400, "order_value_limit_exceeded");
        Assert.That(price.Detail, Is.EqualTo("Максимальная стоимость заказа при экспресс-перевозке 900 евро с учётом резерва 10% на изменение курса"));
        Assert.That(price.Errors!["sellerPrice"], Is.EqualTo(new[] { price.Detail }));
        foreach (var code in new[] { "invalid_order_product_name", "invalid_order_seller_price", "invalid_order_color", "invalid_order_size" })
            Assert.That(factory.Create(new DefaultHttpContext(), 400, code).Errors, Has.Count.EqualTo(1));
    }

    [Test]
    public void SnapshotPreservesOriginalAndClearsOptionalOverrides()
    {
        var now = DateTimeOffset.Parse("2026-09-15T00:00:00Z");
        var order = new Order(1, 1, "https://shop.example.com/", 1, null, Guid.NewGuid(), now);
        order.SetSubmittedProduct(Product, 1, 2);
        Assert.Throws<InvalidOperationException>(() => order.SetSubmittedProduct(Product, 1, 2));
        order.CorrectProduct(Product with { Quantity = 4, Color = "Red", Size = "L", Comment = "note" }, 3, 4, now.AddTicks(1));
        Assert.That(order.UpdatedAt, Is.EqualTo(now.AddMicroseconds(1)));
        order.CorrectProduct(Product, 5, 6, now.AddSeconds(-1));
        Assert.That(order.UpdatedAt, Is.EqualTo(now.AddMicroseconds(2)));
        Assert.That(OrderService.Effective(order), Is.EqualTo(Product));
        Assert.That(OrderService.Submitted(order), Is.EqualTo(Product));
        Assert.That(order.CreatedLimitUsdRateId, Is.EqualTo(1));
        Assert.That(order.UpdatedLimitEurRateId, Is.EqualTo(6));
        Assert.That(order.AppliedExchangeRateHistoryId, Is.Null);
    }

    private static void Reject(OrderProductDto product, string code)
        => Assert.That(Assert.Throws<ServiceException>(() => OrderProductRules.Validate(product))!.Code, Is.EqualTo(code));
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
