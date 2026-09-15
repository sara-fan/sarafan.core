// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Models;

namespace Sarafan.Core.ModelTests;

[TestFixture]
public sealed class OrderProductContractTests
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-09-14T00:00:00Z");
    private static readonly DateTimeOffset UpdatedAt = DateTimeOffset.Parse("2026-09-14T01:00:00Z");

    [Test]
    public void CurrencyCatalogue_UsesStableIsoNumericValuesAndMetadata()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Enum.GetValues<Currency>(), Is.EqualTo(new[] { Currency.Rub, Currency.Usd, Currency.Eur }));
            Assert.That((int)Currency.Rub, Is.EqualTo(643));
            Assert.That(Currency.Rub.GetDisplayName(), Is.EqualTo("Российский рубль"));
            Assert.That(Currency.Rub.GetRouteAlias(), Is.EqualTo("rub"));
            Assert.That((int)Currency.Usd, Is.EqualTo(840));
            Assert.That(Currency.Usd.GetDisplayName(), Is.EqualTo("Доллар США"));
            Assert.That(Currency.Usd.GetRouteAlias(), Is.EqualTo("usd"));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ((Currency)999).GetDisplayName());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Currency)999).GetRouteAlias());
    }

    [Test]
    public void Order_ValidatesQuantityAndCommentAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewOrder(quantity: 0));
        Assert.Throws<ArgumentException>(() => NewOrder(comment: new string('x', 2001)));
        Assert.DoesNotThrow(() => NewOrder(quantity: 1, comment: new string('x', 2000)));
    }

    [Test]
    public void Order_NormalizesCreationAndUpdateTimesToUtcAndNeverChangesCreationTime()
    {
        var localCreated = new DateTimeOffset(2026, 9, 14, 3, 0, 0, TimeSpan.FromHours(3));
        var order = new Order(1, 1, "https://shop.example.com/product", 1, null, Guid.NewGuid(), localCreated);

        order.SetProductSnapshot(null, null, null, null, null, null, null, null, null, null,
            new DateTimeOffset(2026, 9, 14, 4, 0, 0, TimeSpan.FromHours(3)));

        Assert.That(order.CreatedAt, Is.EqualTo(DateTimeOffset.Parse("2026-09-14T00:00:00Z")));
        Assert.That(order.UpdatedAt, Is.EqualTo(DateTimeOffset.Parse("2026-09-14T01:00:00Z")));
    }

    [Test]
    public void ProductSnapshot_AdvancesUpdateTimeWhenTheProvidedTimeEqualsTheCurrentTime()
    {
        var order = NewOrder();

        order.SetProductSnapshot(
            null, null, null, null, null, null, null, null, null, null, CreatedAt);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(order.CreatedAt, Is.EqualTo(CreatedAt));
            Assert.That(order.UpdatedAt, Is.EqualTo(CreatedAt.AddMicroseconds(1)));
        }
    }

    [Test]
    public void ProductSnapshot_AdvancesUpdateTimeForSubMicrosecondDeltas()
    {
        var order = NewOrder();

        order.SetProductSnapshot(
            null, null, null, null, null, null, null, null, null, null, CreatedAt.AddTicks(1));

        Assert.That(order.UpdatedAt, Is.EqualTo(CreatedAt.AddMicroseconds(1)));
    }

    [Test]
    public void ProductSnapshot_AcceptsCompleteRecognizedDataAndCopiesCharacteristics()
    {
        var order = NewOrder();
        var characteristics = new Dictionary<string, string> { ["Цвет"] = "Синий" };
        var rate = new ExchangeRateHistory
        {
            Id = 7,
            Provider = "CBR",
            Source = "test",
            BaseCurrency = Currency.Usd,
            QuoteCurrency = Currency.Rub,
            Nominal = 1,
            OfficialRate = 81.123456m,
            SourceEffectiveDate = new DateOnly(2026, 9, 13),
            RetrievedAt = DateTimeOffset.Parse("2026-09-13T00:00:00Z")
        };

        order.SetProductSnapshot(
            "Товар",
            "Магазин",
            "https://images.example/product.jpg",
            12.34m,
            Currency.Usd,
            10.25m,
            20.50m,
            30.75m,
            characteristics,
            rate,
            UpdatedAt);
        characteristics["Цвет"] = "Красный";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(order.ProductName, Is.EqualTo("Товар"));
            Assert.That(order.StoreName, Is.EqualTo("Магазин"));
            Assert.That(order.ImageUrl, Is.EqualTo("https://images.example/product.jpg"));
            Assert.That(order.SellerPrice, Is.EqualTo(12.34m));
            Assert.That(order.SellerPriceCurrency, Is.EqualTo(Currency.Usd));
            Assert.That(order.LengthCm, Is.EqualTo(10.25m));
            Assert.That(order.WidthCm, Is.EqualTo(20.50m));
            Assert.That(order.HeightCm, Is.EqualTo(30.75m));
            Assert.That(order.Characteristics, Is.EqualTo(new Dictionary<string, string> { ["Цвет"] = "Синий" }));
            Assert.That(order.AppliedExchangeRateHistoryId, Is.EqualTo(7));
            Assert.That(order.AppliedExchangeRateHistory, Is.SameAs(rate));
            Assert.That(order.CreatedAt, Is.EqualTo(CreatedAt));
            Assert.That(order.UpdatedAt, Is.EqualTo(UpdatedAt));
        }
    }

    [Test]
    public void ProductSnapshot_RejectsIncompleteOrInvalidMoneyDimensionsAndRate()
    {
        var rubRate = new ExchangeRateHistory
        {
            Id = 8,
            Provider = "test",
            Source = "test",
            BaseCurrency = Currency.Rub,
            QuoteCurrency = Currency.Usd,
            Nominal = 1,
            OfficialRate = 1,
            SourceEffectiveDate = new DateOnly(2026, 9, 13),
            RetrievedAt = DateTimeOffset.Parse("2026-09-13T00:00:00Z")
        };

        Assert.Throws<ArgumentException>(() => Snapshot(NewOrder(), sellerPrice: 10, currency: null));
        Assert.Throws<ArgumentException>(() => Snapshot(NewOrder(), sellerPrice: null, currency: Currency.Usd));
        Assert.Throws<ArgumentException>(() => Snapshot(NewOrder(), sellerPrice: 0, currency: Currency.Usd));
        Assert.Throws<ArgumentOutOfRangeException>(() => Snapshot(NewOrder(), sellerPrice: 10, currency: (Currency)999));
        Assert.Throws<ArgumentException>(() => Snapshot(NewOrder(), length: 1, width: null, height: 1));
        Assert.Throws<ArgumentException>(() => Snapshot(NewOrder(), length: 1, width: 0, height: 1));
        Assert.Throws<ArgumentException>(() => Snapshot(
            NewOrder(), sellerPrice: 10, currency: Currency.Usd, rate: rubRate));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewOrder().SetProductSnapshot(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            CreatedAt.AddMicroseconds(-1)));
        var maximumTimestampOrder = new Order(
            1, 1, "https://shop.example.com/product", 1, null, Guid.NewGuid(), DateTimeOffset.MaxValue);
        Assert.Throws<ArgumentOutOfRangeException>(() => maximumTimestampOrder.SetProductSnapshot(
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.MaxValue));
    }

    private static Order NewOrder(int quantity = 1, string? comment = null)
        => new(1, 1, "https://shop.example.com/product", quantity, comment, Guid.NewGuid(), CreatedAt);

    private static void Snapshot(
        Order order,
        decimal? sellerPrice = null,
        Currency? currency = null,
        decimal? length = null,
        decimal? width = null,
        decimal? height = null,
        ExchangeRateHistory? rate = null)
        => order.SetProductSnapshot(
            null,
            null,
            null,
            sellerPrice,
            currency,
            length,
            width,
            height,
            null,
            rate,
            UpdatedAt);
}
