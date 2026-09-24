// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Authentication;
using Sarafan.Core.Services;
using Sarafan.Core.RestModels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Sarafan.Core.Tests;

[TestFixture]
[NonParallelizable]
public sealed class PhoneNormalizerTests
{
    private readonly PhoneNormalizer _normalizer = new();

    [TestCase("+7 (999) 123-45-67", "+79991234567")]
    [TestCase("+79991234567", "+79991234567")]
    [TestCase("89991234567", "+79991234567")]
    [TestCase("  89991234567  ", "+79991234567")]
    [TestCase(" +7)999(123--45 67 ", "+79991234567")]
    public void TryNormalize_ValidPhone_ReturnsE164(string value, string expected)
    {
        var success = _normalizer.TryNormalize(value, out var result);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(success, Is.True);
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(_normalizer.TryNormalize(value, out var detailed, out var reason), Is.True);
            Assert.That(detailed, Is.EqualTo(expected));
            Assert.That(reason, Is.Null);
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

    [TestCase(null, PhoneValidationReason.Empty)]
    [TestCase(" ", PhoneValidationReason.Empty)]
    [TestCase("892100011", PhoneValidationReason.TooShort)]
    [TestCase("8 921 000 11", PhoneValidationReason.TooShort)]
    [TestCase("+792100011", PhoneValidationReason.TooShort)]
    [TestCase("892100011123", PhoneValidationReason.TooLong)]
    [TestCase("+792100011123", PhoneValidationReason.TooLong)]
    [TestCase("8 921 000 11 12", PhoneValidationReason.FormattedDomesticNumber)]
    [TestCase("9210001112", PhoneValidationReason.WrongPrefix)]
    [TestCase("+4915112345678", PhoneValidationReason.WrongPrefix)]
    [TestCase("+", PhoneValidationReason.WrongPrefix)]
    [TestCase("+7abc", PhoneValidationReason.UnsupportedCharacters)]
    [TestCase("+7.9210001112", PhoneValidationReason.UnsupportedCharacters)]
    [TestCase("+7\u00a09210001112", PhoneValidationReason.UnsupportedCharacters)]
    [TestCase("+7９２１０００１１１２", PhoneValidationReason.UnsupportedCharacters)]
    [TestCase("+7+9210001112", PhoneValidationReason.UnsupportedCharacters)]
    [TestCase("8921+0001112", PhoneValidationReason.UnsupportedCharacters)]
    public void ValidationReason_ExplainsFailureInPriorityOrder(string? value, PhoneValidationReason expected)
    {
        Assert.That(_normalizer.TryNormalize(value, out var normalized, out var reason), Is.False);
        Assert.That(normalized, Is.Empty);
        Assert.That(reason, Is.EqualTo(expected));
    }

    private static readonly (string Phone, PhoneValidationReason Reason, string Detail)[] Failures =
    [
        ("", PhoneValidationReason.Empty, "Введите номер телефона."),
        ("+7abc", PhoneValidationReason.UnsupportedCharacters, "Используйте цифры, обычные пробелы, скобки и дефисы. Знак + допускается только в начале номера."),
        ("9210001112", PhoneValidationReason.WrongPrefix, "Начните номер с +7 или 8. Например: +7 (921) 123-45-67."),
        ("892100011", PhoneValidationReason.TooShort, "Номер слишком короткий. Введите 11 цифр, начиная с 8 или +7."),
        ("892100011123", PhoneValidationReason.TooLong, "Номер слишком длинный. Введите 11 цифр, начиная с 8 или +7."),
        ("8 921 000 11 12", PhoneValidationReason.FormattedDomesticNumber, "Для номера с пробелами, скобками или дефисами замените начальную 8 на +7. Например: +7 (921) 123-45-67.")
    ];

    [Test]
    public void Catalogue_UsesTypedReasonOnlyForInvalidPhone()
    {
        var factory = new SarafanProblemDetailsFactory();
        foreach (var failure in Failures)
        {
            var problem = factory.Create(new DefaultHttpContext(), 400, "invalid_phone", phoneValidationReason: failure.Reason);
            Assert.That(problem.Detail, Is.EqualTo(failure.Detail));
        }
        var fallback = factory.Create(new DefaultHttpContext(), 400, "invalid_phone");
        Assert.That(factory.Create(new DefaultHttpContext(), 400, "invalid_phone",
            phoneValidationReason: (PhoneValidationReason)999).Detail, Is.EqualTo(fallback.Detail));
        Assert.That(factory.Create(new DefaultHttpContext(), 400, "invalid_auth_request",
            phoneValidationReason: PhoneValidationReason.TooShort).Detail,
            Is.EqualTo(factory.Create(new DefaultHttpContext(), 400, "invalid_auth_request").Detail));
    }

    [TestCase("phone/resolve")]
    [TestCase("code/request")]
    [TestCase("code/verify")]
    public async Task AuthenticationEndpoints_ReturnSpecificExplanationWithoutEchoingPhone(string endpoint)
    {
        IntegrationTestEnvironment.Factory.Services.GetRequiredService<VerificationAttemptStore>().Reset();
        using var client = IntegrationTestEnvironment.Factory.CreateClient();
        foreach (var failure in Failures)
        {
            using var response = await client.PostAsJsonAsync($"/api/v1/auth/{endpoint}", new { phone = failure.Phone, code = "1112" });
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
            var problem = (await response.Content.ReadFromJsonAsync<SarafanProblemDetails>())!;
            if (failure.Reason == PhoneValidationReason.Empty)
            {
                Assert.That(problem.Code, Is.EqualTo("validation_failed"));
                Assert.That(problem.Errors!["phone"], Does.Contain(failure.Detail));
            }
            else
            {
                Assert.That(problem.Code, Is.EqualTo("invalid_phone"));
                Assert.That(problem.Type, Is.EqualTo("https://sarafan.sw.consulting/problems/invalid-phone"));
                Assert.That(problem.Title, Is.EqualTo("Некорректный номер телефона"));
                Assert.That(problem.Detail, Is.EqualTo(failure.Detail));
                Assert.That(problem.Detail, Does.Not.Contain(failure.Phone));
            }
        }
    }
}
