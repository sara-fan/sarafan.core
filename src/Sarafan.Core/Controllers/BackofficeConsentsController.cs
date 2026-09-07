// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sarafan.Core.Authentication;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[Authorize(Policy = BackofficePolicies.ManageLegalDocuments), Route("api/v1/backoffice/consents")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BackofficeConsentsController(ConsentService consents, ConsentRightsService rights, SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    [HttpGet("customers/{id:int}")]
    public async Task<ActionResult> Customer(int id, CancellationToken token) => Ok(await consents.CustomerAsync(id, token));
    [HttpGet("rights")]
    public async Task<ActionResult> Cases([FromQuery] int? customerId, CancellationToken token) => Ok(await rights.ListAsync(customerId, token));
    [HttpPut("rights/{id:guid}")]
    public async Task<ActionResult> Update(Guid id, RightsCaseUpdate request, CancellationToken token) => Ok(await rights.UpdateAsync(id, request, CurrentBackofficeUserId(), token));
}
