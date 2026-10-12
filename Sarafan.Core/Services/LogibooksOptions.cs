// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.Extensions.Options;

namespace Sarafan.Core.Services;

public sealed class LogibooksOptions
{
    public const int MaximumOutboundBytes = 32 * 1024 * 1024;
    public const int MaximumResponseBytes = 4 * 1024 * 1024;
    public bool Enabled { get; set; }
    // Deployment base without /api. Credentials never enter persistent operations.
    public string BaseUrl { get; set; } = "";
    public string Identifier { get; set; } = "";
    public string Password { get; set; } = "";
    public int DeadlineSeconds { get; set; } = 120;
    public int OutboundBytes { get; set; } = MaximumOutboundBytes;
    public int ResponseBytes { get; set; } = MaximumResponseBytes;
    internal string Target => BaseUrl?.TrimEnd('/') ?? "";
}

internal sealed class LogibooksOptionsValidator : IValidateOptions<LogibooksOptions>
{
    public ValidateOptionsResult Validate(string? name, LogibooksOptions options)
    {
        if (options.DeadlineSeconds is < 1 or > 3600 || options.OutboundBytes is < 1024 or > LogibooksOptions.MaximumOutboundBytes || options.ResponseBytes is < 1024 or > LogibooksOptions.MaximumResponseBytes)
            return ValidateOptionsResult.Fail("Logibooks resource limits must be positive and bounded.");
        if (options.Enabled && (options.Target.Length > 2048 || !Uri.TryCreate(options.Target, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(options.Identifier) || string.IsNullOrWhiteSpace(options.Password)))
            return ValidateOptionsResult.Fail("Enabled Logibooks requires a deployment URL and automated account credentials.");
        return ValidateOptionsResult.Success;
    }
}
