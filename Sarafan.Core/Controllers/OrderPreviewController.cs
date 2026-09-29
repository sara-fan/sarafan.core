// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sarafan.Core.Authentication;

using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[AllowAnonymous]
[Route("api/v1/orders/preview")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OrderPreviewController(
    ProductPreviewService previews,
    AnonymousForecastService forecasts,
    SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    [HttpPost("forecast")]
    [AnonymousApiPolicy(AnonymousApiPolicies.Forecast)]
    [ProducesResponseType<AnonymousForecastDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AnonymousForecastDto>> Forecast(
        AnonymousForecastRequest request, CancellationToken cancellationToken)
        => Ok(await forecasts.CalculateAsync(request, cancellationToken));

    [HttpPost]
    [AnonymousApiPolicy(AnonymousApiPolicies.ProductPreview)]
    [ProducesResponseType<ProductPreviewDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ProductPreviewDto>> Preview(
        ProductPreviewRequest request,
        CancellationToken cancellationToken)
        => Ok(await previews.PreviewAsync(request, cancellationToken));
}
