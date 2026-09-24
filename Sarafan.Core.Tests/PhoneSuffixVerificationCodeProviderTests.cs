// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Authentication;

namespace Sarafan.Core.Tests;

[TestFixture]
public sealed class PhoneSuffixVerificationCodeProviderTests
{
    private readonly PhoneSuffixVerificationCodeProvider _provider = new();

    [TestCase("+79991234567", "4567", true)]
    [TestCase("+79991234567", " 4567 ", true)]
    [TestCase("+79991234567", "1111", false)]
    [TestCase("+79991234567", "", false)]
    [TestCase("+79991234567", null, false)]
    [TestCase("123", "", false)]
    public async Task VerifyCodeAsync_UsesLastFourPhoneDigits(
        string phone,
        string? code,
        bool expected)
    {
        var verified = await _provider.VerifyCodeAsync(
            phone,
            code,
            CancellationToken.None);

        Assert.That(verified, Is.EqualTo(expected));
    }

    [Test]
    public void Operations_HonorCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        using (Assert.EnterMultipleScope())
        {
            Assert.ThrowsAsync<OperationCanceledException>(() =>
                _provider.RequestCodeAsync("+79991234567", source.Token));
            Assert.ThrowsAsync<OperationCanceledException>(() =>
                _provider.VerifyCodeAsync("+79991234567", "4567", source.Token));
        }
    }
}
