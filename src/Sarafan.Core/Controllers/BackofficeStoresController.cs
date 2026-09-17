// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sarafan.Core.Authentication;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[Authorize(Policy = BackofficePolicies.ViewStores)]
[Route("api/v1/backoffice/stores")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BackofficeStoresController(StoreService stores, SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    private string[] Roles => User.FindAll("role").Select(claim => claim.Value).ToArray();

    [HttpGet]
    public async Task<ActionResult<StoreListDto<StaffStoreDto>>> List([FromQuery] StoreStatus? status, CancellationToken token)
        => Ok(await stores.ListStaffAsync(status, Roles, token));

    [HttpGet("ops")]
    public ActionResult<StoreOpsDto> Operations() => Ok(StoreRules.Operations(Roles));

    [HttpGet("{id:int}")]
    public async Task<ActionResult<StaffStoreDto>> Get(int id, CancellationToken token)
        => Ok(await stores.GetStaffAsync(id, Roles, token));

    [HttpGet("{id:int}/logo")]
    public async Task<ActionResult> Logo(int id, CancellationToken token)
    {
        var logo = await stores.GetLogoAsync(id, Roles, token);
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(logo.Content, logo.ContentType);
    }

    [HttpPost]
    [Authorize(Policy = BackofficePolicies.CreateStore)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(StoreRules.RequestMaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = StoreRules.RequestMaxBytes)]
    public async Task<ActionResult<StaffStoreDto>> Create([FromForm] StoreWriteRequest request, CancellationToken token)
    {
        var store = await stores.CreateAsync(request, Roles, token);
        return CreatedAtAction(nameof(Get), new { id = store.Id }, store);
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = BackofficePolicies.EditStore)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(StoreRules.RequestMaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = StoreRules.RequestMaxBytes)]
    public async Task<ActionResult<StaffStoreDto>> Update(int id, [FromForm] StoreWriteRequest request, CancellationToken token)
        => Ok(await stores.UpdateAsync(id, request, Roles, token));

    [HttpDelete("{id:int}")]
    [Authorize(Policy = BackofficePolicies.DeleteStore)]
    public async Task<ActionResult> Delete(int id, DeleteStoreRequest request, CancellationToken token)
    {
        await stores.DeleteAsync(id, request.Version, Roles, token);
        return NoContent();
    }
}
