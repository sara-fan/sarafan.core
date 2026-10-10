// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json.Serialization;
using Sarafan.Core.Models;

namespace Sarafan.Core.RestModels;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderPricingInputs(
    [property: JsonConverter(typeof(ManualPricingAmountsJsonConverter))]
    Dictionary<ServiceKind, decimal> ManualAmounts,
    ServiceKind[] SelectedServices)
{
    public static OrderPricingInputs Empty => new([], []);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderPricingWriteRequest(DateTimeOffset? ExpectedUpdatedAt, OrderPricingInputs? Inputs);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmOrderPricingRequest(DateTimeOffset? ExpectedUpdatedAt);

public sealed record PriceComponentDto(ServiceKind Service, PriceComponentState State, Currency Currency,
    decimal? Amount, decimal? AmountRub, ServiceCatalogueEntryDto? Tariff);
public sealed record OrderPriceCalculationDto(DateTimeOffset CalculatedAt, OrderAppliedExchangeRateDto? ExchangeRate,
    PriceComponentDto[] Components, decimal? TotalRub, OrderPricingInputs Inputs);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnonymousForecastSellerPriceDto(decimal Amount, Currency Currency);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnonymousForecastRequest(AnonymousForecastSellerPriceDto? SellerPrice, int Quantity);
public sealed record AnonymousForecastDto(DateTimeOffset CalculatedAt, decimal? TotalRub);
public sealed record OrderPricingDto(string OrderNumber, DateTimeOffset UpdatedAt, bool CanEdit, bool CanConfirm,
    bool Confirmed, bool Expired, DateTimeOffset? ValidUntil, OrderPriceCalculationDto Calculation,
    ServiceCatalogueEntryDto[] ActiveTariffs);
public sealed record OrderPricingOpsDto(ServiceCatalogueOpsDto Catalogue, EnumOpsItemDto[] ComponentStates,
    int ValidityHours, bool CanManage);

public enum CustomerPricingState { Forecast = 0, Confirmed = 100, Expired = 200 }

public sealed record CustomerPricingDto(CustomerPricingState State, decimal? TotalRub,
    DateTimeOffset? CalculatedAt, DateTimeOffset? ValidUntil, DateTimeOffset AsOf,
    decimal? DomesticDeliveryRub, decimal? CustomsRub)
{
    public bool CustomsPaid { get; init; }
}

public sealed record OrderForecastRequest(OrderSellerPriceDto? SellerPrice, int? Quantity);
