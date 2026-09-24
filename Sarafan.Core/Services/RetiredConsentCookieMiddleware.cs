// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.Extensions.Options;
using Sarafan.Core.Authentication;

namespace Sarafan.Core.Services;

/// <summary>One-release cleanup for the retired browser receipt; remove after the transition release.</summary>
public sealed class RetiredConsentCookieMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, IOptions<AuthenticationOptions> options)
    {
        if (context.Request.Path.StartsWithSegments("/api/v1")
            && context.Request.Cookies.ContainsKey("sarafan.consent-browser"))
        {
            context.Response.Cookies.Delete("sarafan.consent-browser", new CookieOptions
            {
                Path = "/api/v1",
                HttpOnly = true,
                Secure = options.Value.SecureCookies,
                SameSite = SameSiteMode.Strict,
                IsEssential = true
            });
        }
        return next(context);
    }
}
