// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.RestModels;

public sealed record OrderStatusOpsItemDto(
    int Value,
    string Name,
    string RouteAlias,
    int UpperStatusValue,
    string UpperStatusName,
    string UpperStatusRouteAlias);

public sealed record OrderOpsDto(IReadOnlyList<OrderStatusOpsItemDto> Statuses);
