// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class Order
{
    private Order()
    {
    }

    internal Order(
        int customerId,
        long customerOrderNumber,
        string sourceUrl,
        int quantity,
        string? comment,
        Guid creationIdempotencyKey,
        DateTimeOffset createdAt)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Order quantity must be positive.");
        }

        if (comment?.Length > 2000)
        {
            throw new ArgumentException("Order comments cannot exceed 2000 characters.", nameof(comment));
        }

        CustomerId = customerId;
        CustomerOrderNumber = customerOrderNumber;
        SourceUrl = sourceUrl;
        Quantity = quantity;
        Comment = comment;
        CreationIdempotencyKey = creationIdempotencyKey;
        CreatedAt = NormalizeToPostgresTimestamp(createdAt);
        UpdatedAt = CreatedAt;
    }

    public long Id { get; private set; }
    public int CustomerId { get; private set; }
    public long CustomerOrderNumber { get; private set; }
    public OrderStatus Status { get; private set; } = OrderStatus.UnderReview;
    public string SourceUrl { get; private set; } = string.Empty;
    public string? ProductName { get; private set; }
    public string? StoreName { get; private set; }
    public string? ImageUrl { get; private set; }
    public decimal? SellerPrice { get; private set; }
    public Currency? SellerPriceCurrency { get; private set; }
    public decimal? LengthCm { get; private set; }
    public decimal? WidthCm { get; private set; }
    public decimal? HeightCm { get; private set; }
    public Dictionary<string, string>? Characteristics { get; private set; }
    public int Quantity { get; private set; }
    public string? Comment { get; private set; }
    public long? AppliedExchangeRateHistoryId { get; private set; }
    internal Guid CreationIdempotencyKey { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public string? SubmittedProductName { get; private set; }
    public decimal? SubmittedSellerPrice { get; private set; }
    public Currency? SubmittedSellerPriceCurrency { get; private set; }
    public string? SubmittedColor { get; private set; }
    public string? SubmittedSize { get; private set; }
    public string? OverrideProductName { get; private set; }
    public string? OverrideStoreName { get; private set; }
    public decimal? OverrideSellerPrice { get; private set; }
    public Currency? OverrideSellerPriceCurrency { get; private set; }
    public string? OverrideColor { get; private set; }
    public string? OverrideSize { get; private set; }
    public int? OverrideQuantity { get; private set; }
    public string? OverrideComment { get; private set; }
    public long? CreatedLimitUsdRateId { get; private set; }
    public long? CreatedLimitEurRateId { get; private set; }
    public long? UpdatedLimitUsdRateId { get; private set; }
    public long? UpdatedLimitEurRateId { get; private set; }

    internal void SetSubmittedProduct(Sarafan.Core.RestModels.OrderProductDto product, long usdRateId, long eurRateId)
    {
        if (SubmittedProductName is not null) throw new InvalidOperationException("The submitted product is immutable.");
        SubmittedProductName = product.ProductName;
        SubmittedSellerPrice = product.SellerPrice?.Amount;
        SubmittedSellerPriceCurrency = product.SellerPrice?.Currency;
        SubmittedColor = product.Color;
        SubmittedSize = product.Size;
        CreatedLimitUsdRateId = usdRateId;
        CreatedLimitEurRateId = eurRateId;
    }

    internal void CorrectProduct(string? storeName, Sarafan.Core.RestModels.OrderProductDto product,
        long usdRateId, long eurRateId, DateTimeOffset now)
    {
        OverrideStoreName = storeName;
        OverrideProductName = product.ProductName;
        OverrideSellerPrice = product.SellerPrice?.Amount;
        OverrideSellerPriceCurrency = product.SellerPrice?.Currency;
        OverrideQuantity = product.Quantity;
        OverrideColor = product.Color;
        OverrideSize = product.Size;
        OverrideComment = product.Comment;
        UpdatedLimitUsdRateId = usdRateId;
        UpdatedLimitEurRateId = eurRateId;
        var timestamp = NormalizeToPostgresTimestamp(now);
        UpdatedAt = timestamp > UpdatedAt ? timestamp : UpdatedAt.AddMicroseconds(1);
    }

    public Customer Customer { get; private set; } = null!;
    public ExchangeRateHistory? AppliedExchangeRateHistory { get; private set; }

    internal void SetProductSnapshot(
        string? productName,
        string? storeName,
        string? imageUrl,
        decimal? sellerPrice,
        Currency? sellerPriceCurrency,
        decimal? lengthCm,
        decimal? widthCm,
        decimal? heightCm,
        IReadOnlyDictionary<string, string>? characteristics,
        ExchangeRateHistory? appliedExchangeRateHistory,
        DateTimeOffset updatedAt)
    {
        if (sellerPrice.HasValue != sellerPriceCurrency.HasValue || sellerPrice is <= 0)
        {
            throw new ArgumentException("Seller price and currency must both be supplied and the price must be positive.");
        }

        if (sellerPriceCurrency is { } currency && !Enum.IsDefined(currency))
        {
            throw new ArgumentOutOfRangeException(nameof(sellerPriceCurrency), currency, "Seller price currency is not supported.");
        }

        var dimensionCount = new[] { lengthCm, widthCm, heightCm }.Count(value => value.HasValue);
        if (dimensionCount is not (0 or 3) || lengthCm is <= 0 || widthCm is <= 0 || heightCm is <= 0)
        {
            throw new ArgumentException("Dimensions must all be supplied and must be positive.");
        }

        if (appliedExchangeRateHistory is not null
            && (!sellerPriceCurrency.HasValue
                || appliedExchangeRateHistory.BaseCurrency != sellerPriceCurrency.Value))
        {
            throw new ArgumentException("The applied exchange rate base currency must match the seller price currency.");
        }

        var normalizedUpdatedAt = NormalizeToPostgresTimestamp(updatedAt);
        if (normalizedUpdatedAt < UpdatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updatedAt),
                updatedAt,
                "The order update time cannot precede its previous update time.");
        }

        if (normalizedUpdatedAt == UpdatedAt)
        {
            if (UpdatedAt > DateTimeOffset.MaxValue.AddMicroseconds(-1))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(updatedAt),
                    updatedAt,
                    "The order update time cannot advance beyond its maximum value.");
            }

            normalizedUpdatedAt = UpdatedAt.AddMicroseconds(1);
        }

        ProductName = productName;
        StoreName = storeName;
        ImageUrl = imageUrl;
        SellerPrice = sellerPrice;
        SellerPriceCurrency = sellerPriceCurrency;
        LengthCm = lengthCm;
        WidthCm = widthCm;
        HeightCm = heightCm;
        Characteristics = characteristics?.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        AppliedExchangeRateHistory = appliedExchangeRateHistory;
        AppliedExchangeRateHistoryId = appliedExchangeRateHistory?.Id;
        UpdatedAt = normalizedUpdatedAt;
    }

    private static DateTimeOffset NormalizeToPostgresTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}
