// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Sarafan.Core.Authentication;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed class RetiredConsentCookieTests
{
    [TestCase("/api/v1/status/status", true, true, true)]
    [TestCase("/api/v1/auth/refresh", true, false, true)]
    [TestCase("/api/v1", true, true, true)]
    [TestCase("/api/v10/status", true, true, false)]
    [TestCase("/", true, true, false)]
    [TestCase("/api/v1/status/status", false, true, false)]
    public async Task ExpiresOnlyAnExistingLegacyCookie(string path, bool legacy, bool secure, bool expires)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Headers.Cookie = legacy
            ? "sarafan.consent-browser=old-receipt; sarafan.refresh=keep-session"
            : "sarafan.refresh=keep-session";
        var continued = false;
        await new RetiredConsentCookieMiddleware(_ => { continued = true; return Task.CompletedTask; })
            .InvokeAsync(context, Options.Create(new AuthenticationOptions { SecureCookies = secure }));
        Assert.That(continued, Is.True);
        var headers = context.Response.Headers.SetCookie;
        Assert.That(headers.Count, Is.EqualTo(expires ? 1 : 0));
        if (!expires) return;
        Assert.That(headers[0], Does.StartWith("sarafan.consent-browser=;"));
        Assert.That(headers[0], Does.Contain("path=/api/v1").And.Contain("httponly").And.Contain("samesite=strict"));
        Assert.That(headers[0], Does.Contain("expires=Thu, 01 Jan 1970"));
        Assert.That(headers[0]!.Contains("secure", StringComparison.Ordinal), Is.EqualTo(secure));
        Assert.That(headers[0], Does.Not.Contain("sarafan.refresh"));
    }
}
