// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Mvc.Filters;
using Sarafan.Core.Observability;

namespace Sarafan.Core.Services;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class CookieConsentNotRequiredAttribute : Attribute;

public static class ConsentBrowserCookie
{
    public const string Name = "sarafan.consent-browser";
}

public sealed class MandatoryCookieConsentFilter(
    ConsentService consents,
    SarafanProblemDetailsFactory problems,
    ILogger<MandatoryCookieConsentFilter> logger) : IAsyncResourceFilter
{
    public Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next) => OperationLogging.RunAsync(
        logger,
        $"{typeof(MandatoryCookieConsentFilter).FullName}.{nameof(OnResourceExecutionAsync)}",
        () => "context=[redacted]",
        async () =>
        {
            if (context.ActionDescriptor.EndpointMetadata.OfType<CookieConsentNotRequiredAttribute>().Any())
            {
                await next();
                return;
            }

            var status = await consents.CookieStatusAsync(
                context.HttpContext.Request.Cookies[ConsentBrowserCookie.Name],
                context.HttpContext.RequestAborted);
            if (status.Status != "current")
            {
                context.Result = problems.CreateResult(
                    context.HttpContext,
                    StatusCodes.Status403Forbidden,
                    "cookie_consent_required");
                return;
            }

            await next();
        },
        context.HttpContext.RequestAborted);
}
