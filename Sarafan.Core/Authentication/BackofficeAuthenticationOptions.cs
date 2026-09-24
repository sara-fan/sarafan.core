// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;

namespace Sarafan.Core.Authentication;

public sealed class BackofficeAuthenticationOptions
{
    public const string SectionName = "BackofficeAuthentication";

    [Required]
    public string Issuer { get; set; } = "sarafan.core.backoffice";

    [Required]
    public string Audience { get; set; } = "sarafan.backoffice";

    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(1, 365)]
    public int RefreshTokenDays { get; set; } = 7;

    [Required]
    public string RefreshCookieName { get; set; } = "sarafan.backoffice.refresh";

    [Range(10, 16)]
    public int BCryptWorkFactor { get; set; } = 12;

    public bool SecureCookies { get; set; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SigningKey) || SigningKey.Length < 32)
        {
            throw new InvalidOperationException(
                "BackofficeAuthentication:SigningKey must contain at least 32 characters");
        }
    }

    public void ValidateDistinctFrom(AuthenticationOptions customerOptions)
    {
        if (string.Equals(Issuer, customerOptions.Issuer, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "BackofficeAuthentication:Issuer must differ from Authentication:Issuer");
        }

        if (string.Equals(Audience, customerOptions.Audience, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "BackofficeAuthentication:Audience must differ from Authentication:Audience");
        }

        if (string.Equals(SigningKey, customerOptions.SigningKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "BackofficeAuthentication:SigningKey must differ from Authentication:SigningKey");
        }

        if (string.Equals(RefreshCookieName, customerOptions.RefreshCookieName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "BackofficeAuthentication:RefreshCookieName must differ from Authentication:RefreshCookieName");
        }
    }
}
