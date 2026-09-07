// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Filters;
using Sarafan.Core.Observability;

namespace Sarafan.Core.Services;

// Resource filters run before model binding, including multipart buffering to temporary files.
// The controller still checks again in its write transaction to cover publication/withdrawal races.
public sealed class PersonalDataConsentFilter(ConsentService consents, ILogger<PersonalDataConsentFilter> logger) : IAsyncResourceFilter
{
    public Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next) => OperationLogging.RunAsync(logger,
        $"{typeof(PersonalDataConsentFilter).FullName}.{nameof(OnResourceExecutionAsync)}", () => "context=[redacted]", async () =>
        {
            var subject = context.HttpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(subject, out var customerId)) throw new ServiceException(401, "invalid_access_token");
            await consents.WithPersonalDataAsync(customerId, () => Task.FromResult(true), context.HttpContext.RequestAborted);
            await next();
        }, context.HttpContext.RequestAborted);
}
