// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Sarafan.Core.Data;
using Sarafan.Core.Observability;
using Sarafan.Core.Services;

namespace Sarafan.Core.Authentication;

public sealed class BackofficeJwtBearerEvents(
    AppDbContext database,
    IOptions<BackofficeBootstrapOptions> bootstrapOptions,
    ILogger<BackofficeJwtBearerEvents> logger) : JwtBearerEvents
{
    public override async Task TokenValidated(TokenValidatedContext context)
    {
        var subject = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var version = context.Principal?.FindFirstValue(BackofficeJwtTokenService.TokenVersionClaim);
        var identityType = context.Principal?.FindFirstValue(BackofficeJwtTokenService.IdentityTypeClaim);
        if (!int.TryParse(subject, out var userId)
            || !int.TryParse(version, out var tokenVersion)
            || identityType != BackofficeJwtTokenService.IdentityType)
        {
            context.Fail("Invalid back-office token claims");
            return;
        }

        var current = await database.BackofficeUsers
            .AsNoTracking()
            .Where(item => item.Id == userId)
            .Select(item => new { item.IsActive, item.IsDemo, item.TokenVersion })
            .SingleOrDefaultAsync(context.HttpContext.RequestAborted);
        var realOperationsEnabled = bootstrapOptions.Value.RealOrdersEnabled
            || bootstrapOptions.Value.RealPaymentIntegrationEnabled;
        if (current is null
            || !current.IsActive
            || (current.IsDemo && realOperationsEnabled)
            || current.TokenVersion != tokenVersion)
        {
            context.Fail("Back-office session is no longer valid");
        }
    }

    public override async Task Challenge(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        var challenge = "Bearer";
        if (context.AuthenticateFailure is not null)
        {
            challenge += " error=\"invalid_token\"";
            SarafanEvents.AuthenticationRejected(logger);
        }

        context.HttpContext.Response.Headers.WWWAuthenticate = challenge;
        await context.HttpContext.RequestServices
            .GetRequiredService<SarafanProblemDetailsFactory>()
            .WriteAsync(
                context.HttpContext,
                StatusCodes.Status401Unauthorized,
                "invalid_backoffice_access_token",
                context.HttpContext.RequestAborted);
    }

    public override async Task Forbidden(ForbiddenContext context)
        => await context.HttpContext.RequestServices
            .GetRequiredService<SarafanProblemDetailsFactory>()
            .WriteAsync(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                "access_denied",
                context.HttpContext.RequestAborted);
}
