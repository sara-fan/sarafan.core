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

[Route("api/v1/backoffice/status")]
[Authorize(Policy = BackofficePolicies.Access)]
public sealed class BackofficeStatusController(
    ExchangeRateService exchangeRates, SarafanProblemDetailsFactory problemDetailsFactory)
    : SarafanControllerBase(problemDetailsFactory)
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<BackofficeStatus>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BackofficeStatus>> Status(CancellationToken cancellationToken)
    {
        var rate = await exchangeRates.GetLatestAsync(cancellationToken);
        var eur = await exchangeRates.GetLatestAsync(cancellationToken, Currency.Eur);
        var currencies = Enum.GetValues<Currency>()
            .OrderBy(currency => (int)currency)
            .Select(currency => new EnumOpsItemDto(
                (int)currency,
                currency.GetDisplayName(),
                currency.GetRouteAlias()))
            .ToArray();
        return Ok(new BackofficeStatus(
            "Sarafan.Core",
            "ok",
            VersionInfo.AppVersion,
            new[] { rate, eur }.OfType<ExchangeRateDto>().ToArray(),
            currencies));
    }
}
