// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sarafan.Core.Authentication;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[CookieConsentNotRequired, Authorize(Policy = BackofficePolicies.ManageConsentWithdrawalRequests), Route("api/v1/backoffice/consents")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BackofficeConsentsController(ConsentWithdrawalRequestService withdrawalRequests, SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    [HttpGet("withdrawal-requests")]
    public async Task<ActionResult> Requests(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string sortBy = "processed",
        [FromQuery] string sortOrder = "asc",
        [FromQuery] string? search = null,
        [FromQuery] bool? processed = null,
        CancellationToken token = default)
        => Ok(await withdrawalRequests.ListAsync(page, pageSize, sortBy, sortOrder, search, processed, token));

    [HttpPut("withdrawal-requests/processed")]
    public async Task<ActionResult> Process(ProcessConsentWithdrawalRequest request, CancellationToken token)
        => Ok(await withdrawalRequests.ProcessAsync(request, token));
}
