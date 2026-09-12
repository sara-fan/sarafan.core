// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Authentication;

namespace Sarafan.Core.Tests;

[TestFixture]
public sealed class PhoneNormalizerTests
{
    private readonly PhoneNormalizer _normalizer = new();

    [TestCase("+7 (999) 123-45-67", "+79991234567")]
    [TestCase("+79991234567", "+79991234567")]
    [TestCase("89991234567", "+79991234567")]
    public void TryNormalize_ValidPhone_ReturnsE164(string value, string expected)
    {
        var success = _normalizer.TryNormalize(value, out var result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(success, Is.True);
            Assert.That(result, Is.EqualTo(expected));
        }
    }

    [TestCase("")]
    [TestCase("123")]
    [TestCase("0000000000")]
    [TestCase("not-a-phone")]
    [TestCase("8 999 123 45 67")]
    [TestCase("9991234567")]
    [TestCase("+4915112345678")]
    [TestCase("+7.999.123.45.67")]
    [TestCase("+79991234567 доб. 1")]
    [TestCase("+7\u00a09991234567")]
    public void TryNormalize_InvalidPhone_ReturnsFalse(string value)
    {
        Assert.That(_normalizer.TryNormalize(value, out _), Is.False);
    }
}
