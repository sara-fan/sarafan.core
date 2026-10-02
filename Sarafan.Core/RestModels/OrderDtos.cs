// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;

using Sarafan.Core.Models;

namespace Sarafan.Core.RestModels;

public sealed class CreateOrderRequest
{
    public OrderProductRequest? Product { get; set; }
    public string? SourceUrl { get; set; }

    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [Range(1, int.MaxValue, ErrorMessage = "Количество должно быть положительным числом.")]
    public int? Quantity { get; set; }

    [StringLength(2000, ErrorMessage = "Длина комментария не должна превышать {1} символов.")]
    public string? Comment { get; set; }
}

public sealed class RejectOrderReviewRequest
{
    public DateTimeOffset? ExpectedUpdatedAt { get; set; }
    public string? Reason { get; set; }
}

public sealed record OrderDeliveryEstimateDto(int MinimumDays, int MaximumDays);

public sealed class CancelOrderRequest
{
    public DateTimeOffset? ExpectedUpdatedAt { get; set; }
    public string? Reason { get; set; }
}

public sealed class ProductPreviewRequest
{
    public string? SourceUrl { get; set; }
}

public sealed record ProductPreviewDto(string SourceUrl, string Outcome)
{
    public const string ManualReviewOutcome = "manual_review";
    public const string RecognizedOutcome = "recognized";
    public OrderProductDto? Product { get; init; }
}

public sealed record OrderSellerPriceDto(decimal Amount, Currency Currency);

public sealed record OrderDimensionsDto(decimal LengthCm, decimal WidthCm, decimal HeightCm);

public sealed record OrderAppliedExchangeRateDto(
    long Id,
    string Provider,
    Currency BaseCurrency,
    Currency QuoteCurrency,
    int Nominal,
    decimal OfficialRate,
    DateOnly SourceEffectiveDate);

public sealed record OrderDto(
    string OrderNumber,
    OrderStatus Status,
    string SourceUrl,
    string? ProductName,
    string? StoreName,
    string? ImageUrl,
    OrderSellerPriceDto? SellerPrice,
    OrderDimensionsDto? Dimensions,
    IReadOnlyDictionary<string, string>? Characteristics,
    int Quantity,
    string? Comment,
    OrderAppliedExchangeRateDto? AppliedExchangeRate)
{
    public required OrderProductDto Product { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public bool CanCancel { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }
    public bool ShowReviewFields { get; init; }
    public string? ReviewReason { get; init; }
    public DateTimeOffset? ReviewCompletedAt { get; init; }
    public OrderDeliveryEstimateDto? EstimatedDelivery { get; init; }
    public OrderCheckoutDto? Checkout { get; init; }
    public required CustomerPricingDto Pricing { get; init; }
}

public sealed record CustomerOrderListItemDto(
    string OrderNumber,
    OrderStatus Status,
    string SourceUrl,
    string? ProductName,
    string? StoreName,
    string? ImageUrl,
    OrderSellerPriceDto? SellerPrice,
    int Quantity,
    DateTimeOffset CreatedAt)
{
    public OrderDeliveryEstimateDto? EstimatedDelivery { get; init; }
    public required CustomerPricingDto Pricing { get; init; }
}

public sealed record OrderStatusOpsItemDto(
    int Value,
    string Name,
    string RouteAlias,
    int UpperStatusValue,
    string UpperStatusName,
    string UpperStatusRouteAlias,
    bool IsTerminal,
    int ProgressPercent);

public sealed record CurrencyOpsItemDto(int Value, string Name, string RouteAlias, string Symbol);

public sealed record ProductSourceUrlOpsDto(
    int MaximumLength,
    string TopLevelDomainListVersion,
    IReadOnlyList<string> TopLevelDomains);

public sealed record OrderOpsDto(
    IReadOnlyList<OrderStatusOpsItemDto> Statuses,
    IReadOnlyList<CurrencyOpsItemDto> Currencies,
    ProductSourceUrlOpsDto ProductSourceUrl)
{
    public OrderProductLimitsDto? ProductLimits { get; init; }
    public IReadOnlyList<EnumOpsItemDto> PricingStates { get; init; } = [];
    public IReadOnlyList<OrderCheckoutDeliveryOptionDto> CheckoutDeliveries { get; init; } = [];
}

public sealed record BackofficeOrderStatusFilterGroupDto(
    string RouteAlias,
    string Name,
    IReadOnlyList<OrderStatus> Statuses);

public sealed record BackofficeOrderOpsDto(
    IReadOnlyList<OrderStatusOpsItemDto> Statuses,
    IReadOnlyList<CurrencyOpsItemDto> Currencies,
    IReadOnlyList<BackofficeOrderStatusFilterGroupDto> StatusGroups)
{
    public OrderProductLimitsDto? ProductLimits { get; init; }
}

public sealed record BackofficeOrderListItemDto(
    string OrderNumber,
    OrderStatus Status,
    string SourceUrl,
    string? ProductName,
    string? StoreName,
    OrderSellerPriceDto? SellerPrice,
    int Quantity,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed class BackofficeOrderPageDto : PagedResult<BackofficeOrderListItemDto>
{
    public OrderStatus? Status { get; init; }
    public string? StatusGroup { get; init; }
    public DateOnly? CreatedFrom { get; init; }
    public DateOnly? CreatedTo { get; init; }
}
