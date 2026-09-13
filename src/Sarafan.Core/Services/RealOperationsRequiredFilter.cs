// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

using Sarafan.Core.Authentication;
using Sarafan.Core.Observability;

namespace Sarafan.Core.Services;

public sealed class RealOperationsRequiredFilter(
    IOptions<BackofficeBootstrapOptions> options,
    SarafanProblemDetailsFactory problems,
    ILogger<RealOperationsRequiredFilter> logger) : IAsyncResourceFilter
{
    public Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(RealOperationsRequiredFilter).FullName}.{nameof(OnResourceExecutionAsync)}",
            () => "context=[redacted]",
            async () =>
            {
                if (!BackofficeUserService.RealOperationsEnabled(options.Value))
                {
                    context.Result = problems.CreateResult(
                        context.HttpContext,
                        StatusCodes.Status404NotFound,
                        "resource_not_found");
                    return;
                }

                await next();
            },
            context.HttpContext.RequestAborted);
}
