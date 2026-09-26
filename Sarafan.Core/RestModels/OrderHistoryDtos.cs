// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Models;

namespace Sarafan.Core.RestModels;

public sealed record OrderHistoryOpsDto(EnumOpsItemDto[] Kinds, EnumOpsItemDto[] Areas, EnumOpsItemDto[] ActorTypes);
public sealed record OrderHistoryItemDto(string EventKey, DateTimeOffset At, OrderHistoryKind Kind,
    OrderHistoryArea Areas, OrderHistoryActor ActorType, string ActorName, bool ActorNameHistorical);
public sealed class OrderHistoryPageDto : PagedResult<OrderHistoryItemDto>
{
    public OrderHistoryArea? Area { get; init; }
    public OrderHistoryActor? ActorType { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
}
public sealed record OrderHistoryEvidence(int Version, OrderStatus? StatusBefore, OrderStatus? StatusAfter, string? SourceUrl);
public sealed record OrderHistoryDetailDto(OrderHistoryItemDto Event, int Version, bool MissingCreationDetails,
    OrderProductDto? ProductBefore, OrderProductDto? ProductAfter, OrderStatus? StatusBefore, OrderStatus? StatusAfter,
    string? SourceUrl, OrderPriceCalculationDto? PricingBefore, OrderPriceCalculationDto? PricingAfter,
    DateTimeOffset? ValidUntilBefore, DateTimeOffset? ValidUntilAfter);
