// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[AllowAnonymous, CookieConsentNotRequired, Route("api/v1/customers")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CustomerOperationsController(SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    [HttpGet("ops")]
    public ActionResult<CustomerOpsDto> Operations() => Ok(new CustomerOpsDto(
        Enum.GetValues<CustomerState>()
            .Select(state => new EnumOpsItemDto((int)state, state.GetDisplayName(), state.GetRouteAlias()))
            .ToArray()));
}
