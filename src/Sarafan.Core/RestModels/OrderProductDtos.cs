// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Models;

namespace Sarafan.Core.RestModels;

// Validation belongs after idempotency resolution, not to nested MVC annotations.
public sealed class OrderProductRequest
{
    public string? ProductName { get; set; }
    public string? StoreName { get; set; }
    public OrderSellerPriceDto? SellerPrice { get; set; }
    public string? Color { get; set; }
    public string? Size { get; set; }
}

public sealed class UpdateOrderProductRequest
{
    public DateTimeOffset? ExpectedUpdatedAt { get; set; }
    public string? StoreName { get; set; }
    public string? ProductName { get; set; }
    public OrderSellerPriceDto? SellerPrice { get; set; }
    public int? Quantity { get; set; }
    public string? Color { get; set; }
    public string? Size { get; set; }
    public string? Comment { get; set; }
}

public sealed record OrderProductDto(string? ProductName, OrderSellerPriceDto? SellerPrice,
    int Quantity, string? Color, string? Size, string? Comment)
{
    public string? StoreName { get; init; }
}

public sealed record OrderLimitCheckDto(decimal MaximumAmount, Currency Currency, bool Available,
    DateOnly? SourceEffectiveDate, decimal? MaximumTotalUsd)
{
    public string ExceededMessage => Services.OrderLimitService.ExceededMessage;
}

public sealed record OrderProductLimitsDto(int MinimumQuantity, int MaximumQuantity,
    int DefaultQuantity, int StoreNameMaximumLength, int ProductNameMaximumLength, int ColorMaximumLength,
    int SizeMaximumLength, int CommentMaximumLength, Currency SellerPriceCurrency,
    decimal MaximumUnitPrice, int PriceDecimalPlaces, OrderLimitCheckDto ValueLimit);

public sealed record BackofficeOrderCustomerDto(string? LastName, string? FirstName,
    string? Patronymic, string Phone, string? Email, string? PassportSeries,
    string? PassportNumber, DateOnly? PassportIssueDate, string? PassportIssuedBy,
    string? Inn, string? PostalCode, string? City, string? Address);

public sealed record BackofficeOrderDetailsDto(string OrderNumber, OrderStatus Status,
    string SourceUrl, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    OrderProductDto Product, BackofficeOrderCustomerDto Customer,
    OrderLimitCheckDto LimitCheck, bool CanEditProduct)
{
    public string? ImageUrl { get; init; }
    public OrderDimensionsDto? Dimensions { get; init; }
    public IReadOnlyDictionary<string, string>? Characteristics { get; init; }
    public DateOnly? SavedLimitSourceEffectiveDate { get; init; }
}
