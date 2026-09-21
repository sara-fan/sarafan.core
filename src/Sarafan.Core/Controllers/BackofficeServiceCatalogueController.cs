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

[Authorize(Policy = BackofficePolicies.ViewServiceCatalogue)]
[Route("api/v1/backoffice/service-catalogue")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BackofficeServiceCatalogueController(
    ServiceCatalogueService catalogue,
    SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    private string[] Roles => User.FindAll("role").Select(claim => claim.Value).ToArray();

    [HttpGet("ops")]
    public async Task<ActionResult<ServiceCatalogueOpsDto>> Operations(CancellationToken token)
        => Ok(await catalogue.OperationsAsync(Roles, token));

    [HttpGet]
    public async Task<ActionResult<ServiceCatalogueListDto>> List(CancellationToken token)
        => Ok(await catalogue.ListAsync(Roles, token));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ServiceCatalogueEntryDto>> Get(long id, CancellationToken token)
        => Ok(await catalogue.GetAsync(id, Roles, token));

    [HttpPost]
    [Authorize(Policy = BackofficePolicies.ManageServiceCatalogue)]
    public async Task<ActionResult<ServiceCatalogueEntryDto>> Create(
        ServiceCatalogueWriteRequest request,
        CancellationToken token)
    {
        var created = await catalogue.CreateAsync(request, CurrentBackofficeUserId(), Roles, token);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:long}")]
    [Authorize(Policy = BackofficePolicies.ManageServiceCatalogue)]
    public async Task<ActionResult<ServiceCatalogueEntryDto>> Update(
        long id,
        ServiceCatalogueWriteRequest request,
        CancellationToken token)
        => Ok(await catalogue.UpdateAsync(id, request, CurrentBackofficeUserId(), Roles, token));

    [HttpDelete("{id:long}")]
    [Authorize(Policy = BackofficePolicies.ManageServiceCatalogue)]
    public async Task<ActionResult> Delete(
        long id,
        DeleteServiceCatalogueEntryRequest request,
        CancellationToken token)
    {
        await catalogue.DeleteAsync(id, request.Version, CurrentBackofficeUserId(), Roles, token);
        return NoContent();
    }

    [HttpGet("audit")]
    public async Task<ActionResult<ServiceCatalogueAuditPageDto>> Audit(
        [FromQuery] string? service,
        [FromQuery] string? action,
        [FromQuery] long? entryId,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string sortBy = "timestamp",
        [FromQuery] string sortOrder = "desc",
        CancellationToken token = default)
        => Ok(await catalogue.AuditAsync(ParseFilter<ServiceKind>(service), ParseFilter<ServiceCatalogueAuditAction>(action),
            entryId, search, page, pageSize,
            sortBy, sortOrder, Roles, token));

    private static TEnum? ParseFilter<TEnum>(string? value) where TEnum : struct, Enum
    {
        if (value is null) return null;
        if (!int.TryParse(value, out var numeric))
            throw new ServiceException(400, "invalid_service_catalogue_audit_filter");
        var parsed = (TEnum)Enum.ToObject(typeof(TEnum), numeric);
        if (!Enum.IsDefined(parsed))
            throw new ServiceException(400, "invalid_service_catalogue_audit_filter");
        return parsed;
    }
}
