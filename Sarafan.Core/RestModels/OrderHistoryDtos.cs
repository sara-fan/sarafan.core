// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Models;

namespace Sarafan.Core.RestModels;

public sealed record OrderHistoryOpsDto(EnumOpsItemDto[] Kinds, EnumOpsItemDto[] Areas, EnumOpsItemDto[] ActorTypes);
public sealed record OrderHistoryItemDto(string EventKey, DateTimeOffset At, OrderHistoryKind Kind,
    OrderHistoryArea Areas, OrderHistoryActor ActorType, string ActorName);
public sealed class OrderHistoryPageDto : PagedResult<OrderHistoryItemDto>
{
    public OrderHistoryArea? Area { get; init; }
    public OrderHistoryActor? ActorType { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
}
public sealed record OrderHistoryEvidence(int Version, OrderStatus? StatusBefore, OrderStatus? StatusAfter, string? SourceUrl, string? CancellationReason = null, string? ReviewReason = null, OrderCheckoutDto? CheckoutBefore = null, OrderCheckoutDto? CheckoutAfter = null, bool? CustomsPaidBefore = null, bool? CustomsPaidAfter = null, LogibooksHistoryEvidence? Logibooks = null);
public sealed record LogibooksHistoryEvidence(Guid OperationId, int RegisterId, int ParcelId,
    int? ReconcilerId, string? ReconcilerName, DateTimeOffset? ReconciledAt, string Source);
public sealed record OrderHistoryDetailDto(OrderHistoryItemDto Event, int Version, bool MissingCreationDetails,
    OrderProductDto? ProductBefore, OrderProductDto? ProductAfter, OrderStatus? StatusBefore, OrderStatus? StatusAfter,
    string? SourceUrl, OrderPriceCalculationDto? PricingBefore, OrderPriceCalculationDto? PricingAfter,
    DateTimeOffset? ValidUntilBefore, DateTimeOffset? ValidUntilAfter)
{
    public LogibooksHistoryEvidence? Logibooks { get; init; }
    public string? CancellationReason { get; init; }
    public string? ReviewReason { get; init; }
    public string? CheckoutDeliveryName { get; init; }
    public bool? CustomsPaidBefore { get; init; }
    public bool? CustomsPaidAfter { get; init; }
}
