// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Sarafan.Core.Authentication;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[Route("api/v1/backoffice/users")]
public sealed class BackofficeUsersController(
    BackofficeUserService userService,
    SarafanProblemDetailsFactory problemDetailsFactory) : SarafanControllerBase(problemDetailsFactory)
{
    [Authorize(Policy = BackofficePolicies.ManageUsers)]
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<BackofficeUserDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BackofficeUserDto>>> List(CancellationToken cancellationToken)
        => Ok(await userService.ListAsync(cancellationToken));

    [Authorize(Policy = BackofficePolicies.Access)]
    [HttpGet("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var currentUserId = CurrentBackofficeUserId();
        if (BackofficeAuthorization.IsAllowed(
                User.FindAll("role").Select(claim => claim.Value),
                BackofficeAction.ManageUsers))
        {
            return Ok(await userService.GetAsync(id, cancellationToken));
        }

        if (id != currentUserId)
        {
            return AccessDeniedProblem();
        }

        return Ok(await userService.GetIdentityAsync(id, cancellationToken));
    }

    [Authorize(Policy = BackofficePolicies.ManageRoles)]
    [HttpGet("ops")]
    [ProducesResponseType<IReadOnlyList<BackofficeRoleDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BackofficeRoleDto>>> Operations(
        CancellationToken cancellationToken)
        => Ok(await userService.ListRolesAsync(cancellationToken));

    [Authorize(Policy = BackofficePolicies.ManageUsers)]
    [HttpPost]
    [ProducesResponseType<BackofficeUserDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<BackofficeUserDto>> Create(
        BackofficeUserCreateRequest request,
        CancellationToken cancellationToken)
    {
        var created = await userService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [Authorize(Policy = BackofficePolicies.ManageUsers)]
    [HttpPut("{id:int}")]
    [ProducesResponseType<BackofficeUserDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BackofficeUserDto>> Update(
        int id,
        BackofficeUserUpdateRequest request,
        CancellationToken cancellationToken)
        => Ok(await userService.UpdateAsync(id, request, cancellationToken));

    [Authorize(Policy = BackofficePolicies.ManageUsers)]
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Disable(int id, CancellationToken cancellationToken)
    {
        await userService.DisableAsync(id, cancellationToken);
        return NoContent();
    }

    [Authorize(Policy = BackofficePolicies.Access)]
    [HttpPut("me")]
    [ProducesResponseType<BackofficeIdentityDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BackofficeIdentityDto>> UpdateSelf(
        BackofficeSelfUpdateRequest request,
        CancellationToken cancellationToken)
        => Ok(await userService.UpdateSelfAsync(
            CurrentBackofficeUserId(),
            request,
            cancellationToken));
}
