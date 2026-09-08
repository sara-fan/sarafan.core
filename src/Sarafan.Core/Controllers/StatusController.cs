// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[Route("api/v1/[controller]")]
public sealed class StatusController(SarafanProblemDetailsFactory problemDetailsFactory)
    : SarafanControllerBase(problemDetailsFactory)
{
    [AllowAnonymous]
    [CookieConsentNotRequired]
    [HttpGet("status")]
    [ProducesResponseType<ServiceStatus>(StatusCodes.Status200OK)]
    public ActionResult<ServiceStatus> Status()
        => Ok(new ServiceStatus("Sarafan.Core", "ok", VersionInfo.AppVersion));
}
