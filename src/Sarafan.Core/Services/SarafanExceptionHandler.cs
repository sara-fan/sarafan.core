// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Diagnostics;

using Sarafan.Core.Observability;

namespace Sarafan.Core.Services;

public sealed class SarafanExceptionHandler(
    SarafanProblemDetailsFactory problemDetailsFactory,
    ILogger<SarafanExceptionHandler> logger) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
        => new(OperationLogging.RunAsync(logger, $"{typeof(SarafanExceptionHandler).FullName}.{nameof(TryHandleAsync)}",
            () => LogValueSummary.Inputs((nameof(httpContext), httpContext), (nameof(exception), exception), (nameof(cancellationToken), cancellationToken)),
            () => TryHandleCoreAsync(httpContext, exception, cancellationToken), cancellationToken));

    private async Task<bool> TryHandleCoreAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!OperationLogging.IsExpected(exception, cancellationToken) && !OperationLogging.WasReported(exception))
        {
            SarafanEvents.UnhandledException(logger, exception);
        }

        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        var (statusCode, code) = exception switch
        {
            ServiceException serviceException => (serviceException.StatusCode, serviceException.Code),
            BadHttpRequestException badRequest => (
                badRequest.StatusCode,
                SarafanProblemDetailsFactory.CodeForStatus(badRequest.StatusCode)),
            _ => (StatusCodes.Status500InternalServerError, "internal_error")
        };

        await problemDetailsFactory.WriteAsync(httpContext, statusCode, code, cancellationToken,
            (exception as ServiceException)?.RequiredDocumentId, (exception as ServiceException)?.ConsentKind);
        return true;
    }
}
