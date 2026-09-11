// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.Extensions.Options;

namespace Sarafan.Core.Authentication;

public sealed class VerificationCodeReleaseGate(
    IVerificationCodeProvider provider,
    IOptions<BackofficeBootstrapOptions> options)
{
    private readonly BackofficeBootstrapOptions _options = options.Value;

    public void EnsureAllowed()
    {
        if ((_options.RealOrdersEnabled || _options.RealPaymentIntegrationEnabled)
            && !provider.IsProductionReady)
        {
            throw new InvalidOperationException(
                "The phone-suffix verification provider is forbidden with real orders or real payment integration");
        }
    }
}
