// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Models;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

internal static class OrderOperationsCatalog
{
    public static OrderOpsDto CreatePublic() => new(
        Statuses(),
        Currencies(),
        new ProductSourceUrlOpsDto(
            ProductSourceUrl.MaximumLength,
            IanaTopLevelDomains.ListVersion,
            IanaTopLevelDomains.All));

    public static BackofficeOrderOpsDto CreateBackoffice() => new(
        Statuses(),
        Currencies(),
        OrderStatusFilterGroups.Definitions
            .Select(group => new BackofficeOrderStatusFilterGroupDto(
                group.RouteAlias,
                group.DisplayName,
                group.Statuses))
            .ToArray());

    private static OrderStatusOpsItemDto[] Statuses()
        => Enum.GetValues<OrderStatus>()
            .OrderBy(status => (int)status)
            .Select(status => new OrderStatusOpsItemDto(
                (int)status,
                status.GetDisplayName(),
                status.GetRouteAlias(),
                status.GetUpperStatusValue(),
                status.GetUpperStatusDisplayName(),
                status.GetUpperStatusRouteAlias(),
                status.IsTerminal(),
                status.GetProgressPercent()))
            .ToArray();

    private static EnumOpsItemDto[] Currencies()
        => Enum.GetValues<Currency>()
            .OrderBy(currency => (int)currency)
            .Select(currency => new EnumOpsItemDto(
                (int)currency,
                currency.GetDisplayName(),
                currency.GetRouteAlias()))
            .ToArray();
}
