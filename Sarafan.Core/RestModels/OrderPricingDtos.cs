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
    ServiceKind[] SelectedServices,
    decimal? DomesticDeliveryRub,
    decimal? CustomsRub)
{
    public static OrderPricingInputs Empty => new([], [], null, null);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderPricingWriteRequest(DateTimeOffset? ExpectedUpdatedAt, OrderPricingInputs? Inputs);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmOrderPricingRequest(DateTimeOffset? ExpectedUpdatedAt);

public sealed record PriceComponentDto(ServiceKind Service, PriceComponentState State, Currency Currency,
    decimal? Amount, decimal? AmountRub, ServiceCatalogueEntryDto? Tariff);
public sealed record OrderPriceCalculationDto(DateTimeOffset CalculatedAt, OrderAppliedExchangeRateDto? ExchangeRate,
    PriceComponentDto[] Components, decimal? TotalRub, OrderPricingInputs Inputs);
public sealed record OrderPricingHistoryDto(long Id, DateTimeOffset At, DateTimeOffset? ValidUntil,
    int? ActorId, string? ActorName, OrderPriceCalculationDto Calculation);
public sealed record OrderPricingDto(string OrderNumber, DateTimeOffset UpdatedAt, bool CanEdit, bool CanConfirm,
    bool Confirmed, bool Expired, DateTimeOffset? ValidUntil, OrderPriceCalculationDto Calculation,
    OrderPricingHistoryDto[] History, ServiceCatalogueEntryDto[] ActiveTariffs);
public sealed record OrderPricingOpsDto(ServiceCatalogueOpsDto Catalogue, EnumOpsItemDto[] ComponentStates,
    int ValidityHours, bool CanManage);
