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

[Route("api/v1/auth")]
public sealed class AuthController(
    AuthenticationService authenticationService,
    IOptions<AuthenticationOptions> options,
    SarafanProblemDetailsFactory problemDetailsFactory) : SarafanControllerBase(problemDetailsFactory)
{
    private readonly AuthenticationOptions _options = options.Value;

    [AllowAnonymous]
    [HttpPost("code/request")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<ActionResult> RequestCode(
        RequestCodeRequest request,
        CancellationToken cancellationToken)
    {
        var onboarding = await authenticationService.RequestCodeAsync(
            request,
            RemoteAddress(),
            cancellationToken);
        return Accepted(new CodeRequestDto(onboarding));
    }

    [AllowAnonymous]
    [HttpPost("code/verify")]
    [ProducesResponseType<AuthenticationSessionDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthenticationSessionDto>> VerifyCode(
        VerifyCodeRequest request,
        CancellationToken cancellationToken)
    {
        var session = await authenticationService.VerifyCodeAsync(
            request,
            RemoteAddress(),
            Request.Headers.UserAgent.FirstOrDefault(),
            cancellationToken);
        SetRefreshCookie(session.RefreshToken);
        return Ok(session.Response);
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    [ProducesResponseType<AuthenticationSessionDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthenticationSessionDto>> Refresh(CancellationToken cancellationToken)
    {
        if (!Request.Cookies.TryGetValue(_options.RefreshCookieName, out var refreshToken)
            || string.IsNullOrWhiteSpace(refreshToken))
        {
            return InvalidRefreshTokenProblem();
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
                Path = "/api/v1/auth",
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
                Path = "/api/v1/auth",
                IsEssential = true
            });
    }
}
