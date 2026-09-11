// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Authentication;

public interface IVerificationCodeProvider
{
    bool IsProductionReady => false;
    Task RequestCodeAsync(string phone, CancellationToken cancellationToken);
    Task<bool> VerifyCodeAsync(string phone, string? code, CancellationToken cancellationToken);
}

public sealed class PhoneSuffixVerificationCodeProvider : IVerificationCodeProvider
{
    public bool IsProductionReady => false;

    public Task RequestCodeAsync(string phone, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<bool> VerifyCodeAsync(string phone, string? code, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expectedCode = phone.Length >= 4 ? phone[^4..] : string.Empty;
        return Task.FromResult(
            expectedCode.Length == 4
            && string.Equals(code?.Trim(), expectedCode, StringComparison.Ordinal));
    }
}
