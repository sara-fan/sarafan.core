// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.IdentityModel.Tokens.Jwt;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sarafan.Core.Authentication;
using Sarafan.Core.Models;
using Sarafan.Core.Services;

namespace Sarafan.Core.ModelTests;

[TestFixture]
public sealed class AuthenticationContractTests
{
    [Test]
    public void EnumOpsMetadataIsStableAndDatabaseFree()
    {
        Assert.That(Enum.GetValues<AuthenticationFlowStep>().Select(value => ((int)value, value.GetRouteAlias(), value.GetDisplayName())),
            Is.EqualTo(new[]
            {
                (0, "code", "Код подтверждения"),
                (1, "agreement", "Пользовательское соглашение"),
                (2, "registration", "Регистрация")
            }));
        Assert.That(Enum.GetValues<CustomerState>().Select(value => ((int)value, value.GetRouteAlias(), value.GetDisplayName())),
            Is.EqualTo(new[]
            {
                (0, "preliminary", "Предварительный"),
                (1, "complete", "Заполненный"),
                (2, "disabled", "Отключённый")
            }));
    }

    [TestCase("+79991234567", "+79991234567")]
    [TestCase("+7 (999) 123-45-67", "+79991234567")]
    [TestCase("89991234567", "+79991234567")]
    public void RussianPhoneFormatsNormalizeToCanonicalValue(string input, string expected)
    {
        Assert.That(new PhoneNormalizer().TryNormalize(input, out var actual), Is.True);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase("9991234567")]
    [TestCase("8 999 123-45-67")]
    [TestCase("+4915112345678")]
    [TestCase("+7.999.123.45.67")]
    [TestCase("+79991234567 ext 1")]
    [TestCase("+7\u00a09991234567")]
    public void UnsupportedPhoneFormatsAreRejected(string input)
        => Assert.That(new PhoneNormalizer().TryNormalize(input, out _), Is.False);

    [Test]
    public void DemoVerificationProviderIsBlockedByEitherRealFeatureFlag()
    {
        var provider = new PhoneSuffixVerificationCodeProvider();
        Assert.That(provider.IsProductionReady, Is.False);

        foreach (var settings in new[]
        {
            new BackofficeBootstrapOptions { RealOrdersEnabled = true },
            new BackofficeBootstrapOptions { RealPaymentIntegrationEnabled = true }
        })
        {
            var gate = new VerificationCodeReleaseGate(provider, Options.Create(settings));
            Assert.That(gate.EnsureAllowed, Throws.TypeOf<InvalidOperationException>());
        }

        Assert.That(new VerificationCodeReleaseGate(provider, Options.Create(new BackofficeBootstrapOptions())).EnsureAllowed,
            Throws.Nothing);
    }

    [Test]
    public void VerificationProviderMustExplicitlyOptInToProductionUse()
    {
        IVerificationCodeProvider provider = new UnclassifiedVerificationCodeProvider();
        Assert.That(provider.IsProductionReady, Is.False);
        Assert.That(new VerificationCodeReleaseGate(provider, Options.Create(new BackofficeBootstrapOptions
        {
            RealOrdersEnabled = true
        })).EnsureAllowed, Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void CustomerAccessTokenCarriesTheCurrentTokenVersion()
    {
        var service = new JwtTokenService(
            Options.Create(new AuthenticationOptions
            {
                SigningKey = "authentication-contract-test-key-32-characters"
            }),
            TimeProvider.System,
            NullLogger<JwtTokenService>.Instance);
        var customer = new Customer
        {
            Id = 42,
            Phone = "+79991234567",
            TokenVersion = 7,
            Profile = new CustomerProfile()
        };

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(service.CreateAccessToken(customer).Token);

        Assert.That(jwt.Claims.Single(claim => claim.Type == JwtTokenService.TokenVersionClaim).Value, Is.EqualTo("7"));
    }

    [Test]
    public void ReactivationUsesTheSameProfileCompletenessRuleAsProfileUpdates()
    {
        Assert.That(CustomerProfileState.Evaluate(new CustomerProfile()), Is.EqualTo(CustomerState.Preliminary));
        Assert.That(CustomerProfileState.Evaluate(new CustomerProfile
        {
            LastName = "Иванов",
            FirstName = "Иван",
            Email = "ivan@example.test",
            Inn = "123456789012",
            PostalCode = "101000",
            City = "Москва",
            Address = "ул. Тестовая, 1"
        }), Is.EqualTo(CustomerState.Complete));
    }

    private sealed class UnclassifiedVerificationCodeProvider : IVerificationCodeProvider
    {
        public Task RequestCodeAsync(string phone, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> VerifyCodeAsync(string phone, string? code, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
