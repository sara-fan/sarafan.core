// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[AllowAnonymous]
[Route("api/v1/orders/forecast")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OrderForecastController(OrderService orders,
    SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    [HttpPost]
    [RequestSizeLimit(32 * 1024)]
    [ProducesResponseType<CustomerPricingDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CustomerPricingDto>> Forecast(OrderForecastRequest request,
        CancellationToken cancellationToken)
        => Ok(await orders.ForecastAsync(request, cancellationToken));
}
