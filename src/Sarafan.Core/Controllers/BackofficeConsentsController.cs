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
    public async Task<ActionResult> Requests(CancellationToken token)
        => Ok(await withdrawalRequests.ListAsync(token));

    [HttpPut("withdrawal-requests/processed")]
    public async Task<ActionResult> Process(ProcessConsentWithdrawalRequest request, CancellationToken token)
        => Ok(await withdrawalRequests.ProcessAsync(request, token));
}
