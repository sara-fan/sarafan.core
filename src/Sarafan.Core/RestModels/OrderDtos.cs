// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Models;

namespace Sarafan.Core.RestModels;

public sealed class CreateOrderRequest
{
    public string? SourceUrl { get; set; }
}

public sealed record OrderDto(
    long Id,
    string OrderNumber,
    OrderStatus Status,
    string SourceUrl);

public sealed record OrderStatusOpsItemDto(
    int Value,
    string Name,
    string RouteAlias,
    int UpperStatusValue,
    string UpperStatusName,
    string UpperStatusRouteAlias);

public sealed record OrderOpsDto(IReadOnlyList<OrderStatusOpsItemDto> Statuses);
