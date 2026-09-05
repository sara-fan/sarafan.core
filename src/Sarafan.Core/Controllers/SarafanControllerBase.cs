// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using Microsoft.AspNetCore.Mvc;

using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[ApiController]
[ProducesResponseType(
    typeof(SarafanProblemDetails),
    StatusCodes.Status400BadRequest,
    SarafanProblemDetailsFactory.MediaType)]
[ProducesResponseType(
    typeof(SarafanProblemDetails),
    StatusCodes.Status401Unauthorized,
    SarafanProblemDetailsFactory.MediaType)]
[ProducesResponseType(
    typeof(SarafanProblemDetails),
    StatusCodes.Status403Forbidden,
    SarafanProblemDetailsFactory.MediaType)]
[ProducesResponseType(
    typeof(SarafanProblemDetails),
    StatusCodes.Status404NotFound,
    SarafanProblemDetailsFactory.MediaType)]
[ProducesResponseType(
    typeof(SarafanProblemDetails),
    StatusCodes.Status409Conflict,
    SarafanProblemDetailsFactory.MediaType)]
[ProducesResponseType(
    typeof(SarafanProblemDetails),
    StatusCodes.Status429TooManyRequests,
    SarafanProblemDetailsFactory.MediaType)]
[ProducesResponseType(
    typeof(SarafanProblemDetails),
    StatusCodes.Status500InternalServerError,
    SarafanProblemDetailsFactory.MediaType)]
[ProducesResponseType(
    typeof(SarafanProblemDetails),
    StatusCodes.Status503ServiceUnavailable,
    SarafanProblemDetailsFactory.MediaType)]
public abstract class SarafanControllerBase(SarafanProblemDetailsFactory problemDetailsFactory) : ControllerBase
{
    protected ActionResult InvalidRefreshTokenProblem()
        => problemDetailsFactory.CreateResult(
            HttpContext,
            StatusCodes.Status401Unauthorized,
            "invalid_refresh_token");

    protected ActionResult InvalidBackofficeRefreshTokenProblem()
        => problemDetailsFactory.CreateResult(
            HttpContext,
            StatusCodes.Status401Unauthorized,
            "invalid_backoffice_refresh_token");

    protected ActionResult AccessDeniedProblem()
        => problemDetailsFactory.CreateResult(
            HttpContext,
            StatusCodes.Status403Forbidden,
            "access_denied");

    protected ActionResult CustomerNotFoundProblem()
        => problemDetailsFactory.CreateResult(
            HttpContext,
            StatusCodes.Status404NotFound,
            "customer_not_found");

    protected ActionResult PhotoNotFoundProblem()
        => problemDetailsFactory.CreateResult(
            HttpContext,
            StatusCodes.Status404NotFound,
            "photo_not_found");

    protected ActionResult InvalidPhotoSizeProblem()
        => problemDetailsFactory.CreateResult(
            HttpContext,
            StatusCodes.Status400BadRequest,
            "invalid_photo_size");

    protected ActionResult InvalidPhotoTypeProblem()
        => problemDetailsFactory.CreateResult(
            HttpContext,
            StatusCodes.Status400BadRequest,
            "invalid_photo_type");

    protected ActionResult InvalidPhotoContentProblem()
        => problemDetailsFactory.CreateResult(
            HttpContext,
            StatusCodes.Status400BadRequest,
            "invalid_photo_content");

    protected int CurrentCustomerId()
    {
        var value = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out var customerId)
            ? customerId
            : throw new ServiceException(
                StatusCodes.Status401Unauthorized,
                "invalid_access_token");
    }

    protected int CurrentBackofficeUserId()
    {
        var value = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out var userId)
            ? userId
            : throw new ServiceException(
                StatusCodes.Status401Unauthorized,
                "invalid_backoffice_access_token");
    }

    protected string RemoteAddress()
        => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
