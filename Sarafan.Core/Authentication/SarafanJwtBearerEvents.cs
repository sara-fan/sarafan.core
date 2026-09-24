// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Logging.Abstractions;

using Sarafan.Core.Observability;
using Sarafan.Core.Services;

namespace Sarafan.Core.Authentication;

public sealed class SarafanJwtBearerEvents : JwtBearerEvents
{
    public override async Task Challenge(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        var challenge = context.Options.Challenge;
        if (context.AuthenticateFailure is not null)
        {
            var separator = challenge.Contains(' ') ? ", " : " ";
            challenge = $"{challenge}{separator}error=\"invalid_token\"";
            var logger = context.HttpContext.RequestServices
                .GetService<ILogger<SarafanJwtBearerEvents>>()
                ?? NullLogger<SarafanJwtBearerEvents>.Instance;
            SarafanEvents.AuthenticationRejected(logger);
        }

        context.HttpContext.Response.Headers.WWWAuthenticate = challenge;
        var factory = context.HttpContext.RequestServices
            .GetRequiredService<SarafanProblemDetailsFactory>();
        await factory.WriteAsync(
            context.HttpContext,
            StatusCodes.Status401Unauthorized,
            "invalid_access_token",
            context.HttpContext.RequestAborted);
    }

    public override async Task Forbidden(ForbiddenContext context)
    {
        var factory = context.HttpContext.RequestServices
            .GetRequiredService<SarafanProblemDetailsFactory>();
        await factory.WriteAsync(
            context.HttpContext,
            StatusCodes.Status403Forbidden,
            "access_denied",
            context.HttpContext.RequestAborted);
    }
}
