// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;

namespace Sarafan.Core.Authentication;

public sealed class BackofficeBootstrapOptions
{
    public const string SectionName = "BackofficeBootstrap";

    public bool Enabled { get; set; }
    public string FirstName { get; set; } = "Maxim";
    public string LastName { get; set; } = "Samsonov";
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool RealOrdersEnabled { get; set; }
    public bool RealPaymentIntegrationEnabled { get; set; }

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (RealOrdersEnabled || RealPaymentIntegrationEnabled)
        {
            throw new InvalidOperationException(
                "BackofficeBootstrap cannot be enabled with real orders or real payment integration");
        }

        if (string.IsNullOrWhiteSpace(FirstName) || FirstName.Trim().Length > 100
            || string.IsNullOrWhiteSpace(LastName) || LastName.Trim().Length > 100)
        {
            throw new InvalidOperationException(
                "BackofficeBootstrap names must contain between 1 and 100 characters");
        }

        if (string.IsNullOrWhiteSpace(Email)
            || Email.Trim().Length > 254
            || !new EmailAddressAttribute().IsValid(Email.Trim()))
        {
            throw new InvalidOperationException("BackofficeBootstrap:Email must be a valid email address");
        }

        BackofficePasswordRules.ValidateConfigurationPassword(Password, "BackofficeBootstrap:Password");
    }
}
