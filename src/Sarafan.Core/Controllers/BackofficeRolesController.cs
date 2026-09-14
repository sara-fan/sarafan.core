// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Sarafan.Core.Authentication;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[Authorize(Policy = BackofficePolicies.ManageRoles)]
[Route("api/v1/backoffice/roles")]
public sealed class BackofficeRolesController(
    BackofficeUserService userService,
    SarafanProblemDetailsFactory problemDetailsFactory) : SarafanControllerBase(problemDetailsFactory)
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<BackofficeRoleDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BackofficeRoleDto>>> List(CancellationToken cancellationToken)
        => Ok(await userService.ListRolesAsync(cancellationToken));
}
