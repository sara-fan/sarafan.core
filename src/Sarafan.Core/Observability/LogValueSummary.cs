// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Mvc;

using Sarafan.Core.Authentication;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Observability;

internal static class LogValueSummary
{
    private static readonly HashSet<string> PrivateListStateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "page", "pageSize", "sortBy", "sortOrder", "search", "processed", "kind", "action", "customerId", "documentId"
    };

    // Only these explicit projections may read values. Never serialize or call ToString on arbitrary input.
    internal static string Inputs(params (string Name, object? Value)[] values)
        => values.Length == 0 ? "none" : string.Join("; ", values.Select(value =>
            $"{value.Name}={(PrivateListStateNames.Contains(value.Name) ? "[redacted]" : Describe(value.Value))}"));

    internal static string Describe(object? value) => value switch
    {
        null => "null",
        CancellationToken token => $"cancellation requested={token.IsCancellationRequested}",
        VerifyCodeRequest request => $"VerifyCodeRequest(purpose={Purpose(request.Purpose)}; phone/code/consents=[redacted])",
        RequestCodeRequest request => $"RequestCodeRequest(purpose={Purpose(request.Purpose)}; phone=[redacted])",
        BackofficeLoginRequest => "BackofficeLoginRequest(email/password=[redacted])",
        BackofficeUserCreateRequest => "BackofficeUserCreateRequest(identity/password/roles=[redacted])",
        BackofficeUserUpdateRequest => "BackofficeUserUpdateRequest(identity/password/roles=[redacted])",
        BackofficeSelfUpdateRequest => "BackofficeSelfUpdateRequest(identity/password=[redacted])",
        CustomerProfileUpdateRequest => "CustomerProfileUpdateRequest([redacted])",
        BackofficeUserDto => "BackofficeUserDto([redacted])",
        IReadOnlyCollection<BackofficeUserDto> users => $"BackofficeUserDto collection(count={users.Count})",
        BackofficeIdentityDto => "BackofficeIdentityDto([redacted])",
        BackofficeRoleDto => "BackofficeRoleDto([redacted])",
        IReadOnlyCollection<BackofficeRoleDto> roles => $"BackofficeRoleDto collection(count={roles.Count})",
        CustomerDto => "CustomerDto([redacted])",
        Customer => "Customer([redacted])",
        BackofficeUser => "BackofficeUser([redacted])",
        AuthenticationSession => "AuthenticationSession(tokens/customer=[redacted])",
        AuthenticationSessionDto => "AuthenticationSessionDto(token/customer=[redacted])",
        BackofficeAuthenticationSession => "BackofficeAuthenticationSession(tokens/user=[redacted])",
        BackofficeAuthenticationSessionDto => "BackofficeAuthenticationSessionDto(token/user=[redacted])",
        AccessTokenResult => "AccessTokenResult(token/expiry=[redacted])",
        IFormFile => "file(content/metadata=[redacted])",
        FileResult => "file result(content/metadata=[redacted])",
        SarafanProblemDetails problem => $"problem(status={problem.Status}; details=[redacted])",
        ServiceStatus status when status.Service == "Sarafan.Core" && status.Status == "ok" && status.AppVersion == VersionInfo.AppVersion
            => $"ServiceStatus(name=Sarafan.Core; status=ok; version={VersionInfo.AppVersion})",
        ServiceStatus => "ServiceStatus([redacted])",
        BackofficeStatus => "BackofficeStatus(version/rates=[redacted])",
        ExchangeRateDto => "ExchangeRateDto(rate/metadata=[redacted])",
        CbrRate => "CbrRate(rate/metadata=[redacted])",
        ObjectResult result => $"status={result.StatusCode ?? StatusCodes.Status200OK}; output={Describe(result.Value)}",
        StatusCodeResult result => $"status={result.StatusCode}; no body",
        EmptyResult => "no body",
        OperationLogging.OperationCompleted => "completed; no return value",
        bool result => result ? "true" : "false",
        _ => "[redacted]"
    };

    private static string Purpose(string? purpose) => purpose?.Trim().ToLowerInvariant() switch
    {
        "register" => "register",
        "login" => "login",
        _ => "other"
    };
}
