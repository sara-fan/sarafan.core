// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using Sarafan.Core.Authentication;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[CookieConsentNotRequired, Route("api/v1/backoffice/auth")]
public sealed class BackofficeAuthController(
    BackofficeAuthenticationService authenticationService,
    BackofficeUserService userService,
    IOptions<BackofficeAuthenticationOptions> options,
    SarafanProblemDetailsFactory problemDetailsFactory) : SarafanControllerBase(problemDetailsFactory)
{
    private readonly BackofficeAuthenticationOptions _options = options.Value;

    [AllowAnonymous]
    [HttpPost("login")]
    [ProducesResponseType<BackofficeAuthenticationSessionDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BackofficeAuthenticationSessionDto>> Login(
        BackofficeLoginRequest request,
        CancellationToken cancellationToken)
    {
        var session = await authenticationService.LoginAsync(
            request,
            RemoteAddress(),
            Request.Headers.UserAgent.FirstOrDefault(),
            cancellationToken);
        SetRefreshCookie(session.RefreshToken);
        return Ok(session.Response);
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    [ProducesResponseType<BackofficeAuthenticationSessionDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BackofficeAuthenticationSessionDto>> Refresh(
        CancellationToken cancellationToken)
    {
        if (!Request.Cookies.TryGetValue(_options.RefreshCookieName, out var refreshToken)
            || string.IsNullOrWhiteSpace(refreshToken))
        {
            return InvalidBackofficeRefreshTokenProblem();
        }

        try
        {
            var session = await authenticationService.RefreshAsync(
                refreshToken,
                RemoteAddress(),
                Request.Headers.UserAgent.FirstOrDefault(),
                cancellationToken);
            SetRefreshCookie(session.RefreshToken);
            return Ok(session.Response);
        }
        catch (ServiceException)
        {
            DeleteRefreshCookie();
            throw;
        }
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Logout(CancellationToken cancellationToken)
    {
        Request.Cookies.TryGetValue(_options.RefreshCookieName, out var refreshToken);
        await authenticationService.LogoutAsync(refreshToken, cancellationToken);
        DeleteRefreshCookie();
        return NoContent();
    }

    [Authorize(Policy = BackofficePolicies.Access)]
    [HttpGet("me")]
    [ProducesResponseType<BackofficeIdentityDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BackofficeIdentityDto>> Me(CancellationToken cancellationToken)
        => Ok(await userService.GetIdentityAsync(CurrentBackofficeUserId(), cancellationToken));

    private void SetRefreshCookie(string refreshToken)
    {
        Response.Cookies.Append(
            _options.RefreshCookieName,
            refreshToken,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = _options.SecureCookies,
                SameSite = SameSiteMode.Strict,
                Path = "/api/v1/backoffice/auth",
                MaxAge = TimeSpan.FromDays(_options.RefreshTokenDays),
                IsEssential = true
            });
    }

    private void DeleteRefreshCookie()
    {
        Response.Cookies.Delete(
            _options.RefreshCookieName,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = _options.SecureCookies,
                SameSite = SameSiteMode.Strict,
                Path = "/api/v1/backoffice/auth",
                IsEssential = true
            });
    }
}
