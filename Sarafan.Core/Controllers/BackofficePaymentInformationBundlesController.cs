// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sarafan.Core.Authentication;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[Authorize(Policy = BackofficePolicies.ManagePaymentInformation)]
[Route("api/v1/backoffice/payment-information-bundles")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BackofficePaymentInformationBundlesController(
    PaymentInformationService bundles, SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    private string[] Roles => User.FindAll("role").Select(claim => claim.Value).ToArray();

    [HttpGet]
    public async Task<ActionResult<PaymentBundlePageDto>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        [FromQuery] string sortBy = "id", [FromQuery] string sortOrder = "desc", [FromQuery] string? search = null,
        [FromQuery] string? state = null, CancellationToken token = default)
        => Ok(await bundles.ListAsync(Roles, page, pageSize, sortBy, sortOrder, search, state, token));

    [HttpGet("ops")]
    public ActionResult<PaymentBundleOpsDto> Operations() => Ok(bundles.Operations(Roles));

    [HttpGet("{id:long}")]
    public async Task<ActionResult<PaymentBundleDto>> Get(long id, CancellationToken token)
        => Ok(await bundles.GetAsync(id, Roles, token));

    [HttpGet("{id:long}/qr")]
    public async Task<ActionResult> Qr(long id, [FromQuery] string? v, CancellationToken token)
    {
        var image = await bundles.QrAsync(id, v, Roles, token);
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(image.Content, image.ContentType);
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(PaymentInformationRules.RequestMaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = PaymentInformationRules.RequestMaxBytes)]
    public async Task<ActionResult<PaymentBundleDto>> Create([FromForm] PaymentInformationWriteRequest request, CancellationToken token)
    {
        var row = await bundles.CreateAsync(request, CurrentBackofficeUserId(), Roles, token);
        return CreatedAtAction(nameof(Get), new { id = row.Id }, row);
    }

    [HttpPut("{id:long}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(PaymentInformationRules.RequestMaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = PaymentInformationRules.RequestMaxBytes)]
    public async Task<ActionResult<PaymentBundleDto>> Update(long id, [FromForm] PaymentInformationWriteRequest request, CancellationToken token)
        => Ok(await bundles.UpdateAsync(id, request, CurrentBackofficeUserId(), Roles, token));

    [HttpPost("{id:long}/copy")]
    public async Task<ActionResult<PaymentBundleDto>> Copy(long id, PaymentBundleVersionRequest request, CancellationToken token)
    {
        var row = await bundles.CopyAsync(id, request.Version, CurrentBackofficeUserId(), Roles, token);
        return CreatedAtAction(nameof(Get), new { id = row.Id }, row);
    }

    [HttpPost("{id:long}/enable")]
    public async Task<ActionResult<PaymentBundleDto>> Enable(long id, EnablePaymentBundleRequest request, CancellationToken token)
        => Ok(await bundles.EnableAsync(id, request, CurrentBackofficeUserId(), Roles, token));

    [HttpPost("{id:long}/disable")]
    public async Task<ActionResult<PaymentBundleDto>> Disable(long id, PaymentBundleVersionRequest request, CancellationToken token)
        => Ok(await bundles.DisableAsync(id, request.Version, CurrentBackofficeUserId(), Roles, token));

    [HttpDelete("{id:long}")]
    public async Task<ActionResult> Delete(long id, PaymentBundleVersionRequest request, CancellationToken token)
    {
        await bundles.DeleteAsync(id, request.Version, CurrentBackofficeUserId(), Roles, token);
        return NoContent();
    }
}
