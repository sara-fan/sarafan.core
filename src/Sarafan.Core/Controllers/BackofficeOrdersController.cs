// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Sarafan.Core.Authentication;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[CookieConsentNotRequired]
[Authorize(Policy = BackofficePolicies.ManualQuotes)]
[Route("api/v1/backoffice/orders")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ServiceFilter(typeof(RealOperationsRequiredFilter), Order = int.MinValue)]
public sealed class BackofficeOrdersController(
    OrderService orders,
    SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    [HttpGet("ops")]
    [ProducesResponseType<BackofficeOrderOpsDto>(StatusCodes.Status200OK)]
    public ActionResult<BackofficeOrderOpsDto> Operations()
        => Ok(OrderOperationsCatalog.CreateBackoffice());

    [HttpGet]
    [ProducesResponseType<BackofficeOrderPageDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BackofficeOrderPageDto>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string sortBy = "createdAt",
        [FromQuery] string sortOrder = "desc",
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? statusGroup = null,
        [FromQuery] string? createdFrom = null,
        [FromQuery] string? createdTo = null,
        CancellationToken cancellationToken = default)
        => Ok(await orders.ListForBackofficeAsync(
            page,
            pageSize,
            sortBy,
            sortOrder,
            search,
            status,
            statusGroup,
            createdFrom,
            createdTo,
            cancellationToken));
}
