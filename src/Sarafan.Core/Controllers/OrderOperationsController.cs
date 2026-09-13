// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[AllowAnonymous, CookieConsentNotRequired, Route("api/v1/orders")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OrderOperationsController(SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    [HttpGet("ops")]
    public ActionResult<OrderOpsDto> Operations() => Ok(new OrderOpsDto(
        Enum.GetValues<OrderStatus>()
            .OrderBy(status => (int)status)
            .Select(status => new OrderStatusOpsItemDto(
                (int)status,
                status.GetDisplayName(),
                status.GetRouteAlias(),
                status.GetUpperStatusValue(),
                status.GetUpperStatusDisplayName(),
                status.GetUpperStatusRouteAlias()))
            .ToArray()));
}
