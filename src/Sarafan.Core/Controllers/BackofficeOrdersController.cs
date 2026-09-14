// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Globalization;

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
        [FromQuery(Name = "page")] string[]? page = null,
        [FromQuery(Name = "pageSize")] string[]? pageSize = null,
        [FromQuery(Name = "sortBy")] string[]? sortBy = null,
        [FromQuery(Name = "sortOrder")] string[]? sortOrder = null,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        // statusGroup is resolved manually from Request.Query to preserve explicit empty values.
        [FromQuery] string? createdFrom = null,
        [FromQuery] string? createdTo = null,
        CancellationToken cancellationToken = default)
        => Ok(await orders.ListForBackofficeAsync(
            ParseListInteger(page, 1),
            ParseListInteger(pageSize, 10),
            ParseListString(sortBy, "createdAt"),
            ParseListString(sortOrder, "desc"),
            search,
            status,
            ParseOptionalQueryValue(Request.Query, "statusGroup"),
            createdFrom,
            createdTo,
            cancellationToken));

    private static int ParseListInteger(string[]? values, int defaultValue)
    {
        if (values is null)
        {
            return defaultValue;
        }

        if (values.Length != 1
            || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ServiceException(StatusCodes.Status400BadRequest, "invalid_order_list_filter");
        }

        return parsed;
    }

    private static string ParseListString(string[]? values, string defaultValue)
    {
        if (values is null)
        {
            return defaultValue;
        }

        if (values.Length != 1)
        {
            throw new ServiceException(StatusCodes.Status400BadRequest, "invalid_order_list_filter");
        }

        return values[0];
    }

    private static string? ParseOptionalQueryValue(IQueryCollection query, string key)
    {
        if (!query.TryGetValue(key, out var values))
        {
            return null;
        }

        if (values.Count != 1)
        {
            throw new ServiceException(StatusCodes.Status400BadRequest, "invalid_order_list_filter");
        }

        return values[0];
    }
}
