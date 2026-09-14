// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[Route("api/v1/consents"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ConsentsController(ConsentService consents, ConsentWithdrawalRequestService withdrawalRequests, SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    [Authorize, HttpGet("me")]
    public async Task<ActionResult> Mine(CancellationToken token) => Ok(await consents.CustomerAsync(CurrentCustomerId(), token));
    [Authorize, HttpPost("me/personal-data")]
    public async Task<ActionResult> PersonalData(ConsentDecisionRequest request, CancellationToken token) => Ok(await consents.DecidePersonalDataAsync(CurrentCustomerId(), request, token));
    [Authorize, HttpPost("me/withdrawal-request")]
    public async Task<ActionResult> RequestWithdrawal(CancellationToken token)
        => Ok(await withdrawalRequests.CreateAsync(CurrentCustomerId(), token));
}
