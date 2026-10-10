// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[Authorize]
[Route("api/v1/payment-information/current")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PaymentInformationController(PaymentInformationService bundles, SarafanProblemDetailsFactory problems)
    : SarafanControllerBase(problems)
{
    [HttpGet]
    public async Task<ActionResult<CurrentPaymentInformationDto>> Current(CancellationToken token)
        => Ok(await bundles.CurrentAsync(token));

    [HttpGet("qr")]
    public async Task<ActionResult> Qr([FromQuery] string? v, CancellationToken token)
    {
        var image = await bundles.QrAsync(null, v, null, token);
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(image.Content, image.ContentType);
    }
}
