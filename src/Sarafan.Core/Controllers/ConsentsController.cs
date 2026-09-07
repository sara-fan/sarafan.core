// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Sarafan.Core.Authentication;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[Route("api/v1/consents"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ConsentsController(ConsentService consents, ConsentRightsService rights, VerificationAttemptStore attempts,
    IOptions<AuthenticationOptions> options, IOptions<ConsentOptions> consentOptions, SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    public const string BrowserCookie = "sarafan.consent-browser";
    [AllowAnonymous, HttpGet("cookies")]
    public async Task<ActionResult> Cookies(CancellationToken token) => Ok(await consents.CookieStatusAsync(Request.Cookies[BrowserCookie], token));
    [AllowAnonymous, HttpPost("cookies")]
    public async Task<ActionResult> DecideCookies(ConsentDecisionRequest request, CancellationToken token)
    {
        if (!attempts.TryConsume($"consent:{RemoteAddress()}", 60, TimeSpan.FromMinutes(15))) throw new ServiceException(429, "rate_limited");
        var raw = Request.Cookies[BrowserCookie];
        // A lost first response can be retried without its Set-Cookie header.
        // The opaque subject remains stable for that random idempotency key.
        if (raw is not { Length: >= 32 and <= 128 }) raw = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(options.Value.SigningKey), Encoding.UTF8.GetBytes("cookie-receipt:" + request.IdempotencyKey)));
        var result = await consents.DecideCookiesAsync(raw, request, token);
        Response.Cookies.Append(BrowserCookie, raw, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.Value.SecureCookies,
            SameSite = SameSiteMode.Strict,
            Path = "/api/v1/consents",
            MaxAge = TimeSpan.FromDays(consentOptions.Value.CookieDays),
            IsEssential = true
        });
        return Ok(result);
    }
    [Authorize, HttpGet("me")]
    public async Task<ActionResult> Mine(CancellationToken token) => Ok(await consents.CustomerAsync(CurrentCustomerId(), token));
    [Authorize, HttpPost("me/personal-data")]
    public async Task<ActionResult> PersonalData(ConsentDecisionRequest request, CancellationToken token) => Ok(await consents.DecidePersonalDataAsync(CurrentCustomerId(), request, token));
    [Authorize, HttpPost("me/browser")]
    public async Task<ActionResult> Associate(CancellationToken token)
    {
        if (!Guid.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Jti), out var authenticationTokenId) || authenticationTokenId == Guid.Empty)
            throw new ServiceException(401, "invalid_access_token");
        await consents.AssociateBrowserAsync(CurrentCustomerId(), Request.Cookies[BrowserCookie], authenticationTokenId, token);
        return NoContent();
    }
    [Authorize, HttpPost("me/rights")]
    public async Task<ActionResult> Rights(RightsRequest request, CancellationToken token) => Ok(await rights.CreateAsync(CurrentCustomerId(), request, token));
}
