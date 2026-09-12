// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Tests;

[TestFixture, NonParallelizable]
public sealed class AuthenticationValidationTests
{
    private static int _phoneSequence = 7_000_000;
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _client = null!;
    private RecordingCodeProvider _provider = null!;

    [SetUp]
    public async Task SetUp()
    {
        _provider = new RecordingCodeProvider();
        _app = IntegrationTestEnvironment.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IVerificationCodeProvider>();
            services.AddSingleton<IVerificationCodeProvider>(_provider);
            services.RemoveAll<VerificationAttemptStore>();
            services.AddSingleton<VerificationAttemptStore>();
        }));
        _client = _app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        await ConsentTestData.AcceptMandatoryCookies(_client);
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Test, Combinatorial]
    public async Task VerificationChecksCodeBeforeRejectingConsent(
        [Values("{\"personalDataConsent\":{}}", "{\"PersonalDataConsent\":{}}",
            "{\"personalDataConsent\":{\"documentId\":\"invalid-guid\"}}", "{\"personalDataConsent\":[]}",
            "{\"termsDocumentId\":\"invalid-guid\"}", "{\"termsAccepted\":{}}")]
        string consentJson,
        [Values] bool withReceipt,
        [Values] bool validCode)
    {
        var phone = NextPhone();
        var receipt = await ConsentTestData.Onboarding(_client, phone);
        if (!withReceipt) await VerifySuccessfully(phone, receipt);
        var before = await ReadAuthenticationState(phone, receipt);
        var calls = _provider.VerifyCalls;
        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(consentJson)!;
        payload["phone"] = phone;
        payload["code"] = validCode ? phone[^4..] : "wrong";
        if (withReceipt) payload["onboardingToken"] = receipt;

        using var response = await _client.PostAsJsonAsync("/api/v1/auth/code/verify", payload);
        var problem = (await response.Content.ReadFromJsonAsync<SarafanProblemDetails>())!;
        var expectedCode = validCode ? "invalid_auth_request" : "invalid_code";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(validCode ? HttpStatusCode.BadRequest : HttpStatusCode.Unauthorized));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
            Assert.That(problem.Code, Is.EqualTo(expectedCode));
            Assert.That(problem.Type, Is.EqualTo($"https://sarafan.sw.consulting/problems/{expectedCode.Replace('_', '-')}"));
            Assert.That(problem.Status, Is.EqualTo((int)response.StatusCode));
            Assert.That(_provider.VerifyCalls, Is.EqualTo(calls + 1));
            Assert.That(response.Headers.Contains("Set-Cookie"), Is.False);
            Assert.That(await ReadAuthenticationState(phone, receipt), Is.EqualTo(before));
        }

        await VerifySuccessfully(phone, withReceipt ? receipt : null);
    }

    [Test, Combinatorial]
    public async Task VerificationAllowsEmptyConsentValuesAndUnrelatedFields(
        [Values("{\"termsAccepted\":false}", "{\"termsAccepted\":null}", "{\"termsDocumentId\":null}",
            "{\"personalDataConsent\":null}", "{\"unrelated\":{}}")]
        string consentJson,
        [Values] bool withReceipt)
    {
        var phone = NextPhone();
        var receipt = await ConsentTestData.Onboarding(_client, phone);
        if (!withReceipt) await VerifySuccessfully(phone, receipt);
        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(consentJson)!;
        payload["phone"] = phone;
        payload["code"] = phone[^4..];
        if (withReceipt) payload["onboardingToken"] = receipt;
        var calls = _provider.VerifyCalls;

        using var response = await _client.PostAsJsonAsync("/api/v1/auth/code/verify", payload);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
            Assert.That(_provider.VerifyCalls, Is.EqualTo(calls + 1));
        }
    }

    [Test]
    public async Task CodeRequestStillValidatesNestedConsentBeforeProviderDispatch()
    {
        var phone = NextPhone();
        await using var scope = _app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var receipts = await database.ConsentOnboarding.CountAsync();

        using var response = await _client.PostAsJsonAsync("/api/v1/auth/code/request", new
        {
            phone,
            personalDataConsent = new { }
        });
        var problem = (await response.Content.ReadFromJsonAsync<SarafanProblemDetails>())!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem.Code, Is.EqualTo("validation_failed"));
            Assert.That(problem.Errors?.Keys, Does.Contain("personalDataConsent.ContentHash"));
            Assert.That(problem.Errors?.Keys, Does.Contain("personalDataConsent.Decision"));
            Assert.That(_provider.RequestCalls, Is.Zero);
            Assert.That(_provider.VerifyCalls, Is.Zero);
            Assert.That(await database.ConsentOnboarding.CountAsync(), Is.EqualTo(receipts));
        }
    }

    [TestCase("{\"phone\":\"\",\"code\":\"1234\"}")]
    [TestCase("{\"phone\":\"+79997000000\",\"code\":\"\"}")]
    [TestCase("{\"phone\":\"+79997000000\",\"code\":\"12345678901234567\"}")]
    [TestCase("{")]
    public async Task VerificationStillRejectsInvalidBasicInputBeforeProvider(string json)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync("/api/v1/auth/code/verify", content);
        var problem = (await response.Content.ReadFromJsonAsync<SarafanProblemDetails>())!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem.Code, Is.EqualTo("validation_failed"));
            Assert.That(_provider.VerifyCalls, Is.Zero);
        }
    }

    private async Task VerifySuccessfully(string phone, string? receipt)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/code/verify", new
        {
            phone,
            code = phone[^4..],
            onboardingToken = receipt
        });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
    }

    private async Task<(int Customers, int Events, int Sessions, DateTimeOffset? UsedAt)> ReadAuthenticationState(
        string phone, string receipt)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customers = database.Customers.Where(item => item.Phone == phone);
        var hash = JwtTokenService.HashRefreshToken(receipt);
        return (await customers.CountAsync(),
            await database.ConsentEvents.CountAsync(item => customers.Any(customer => customer.Id == item.CustomerId)),
            await database.RefreshSessions.CountAsync(item => customers.Any(customer => customer.Id == item.CustomerId)),
            await database.ConsentOnboarding.Where(item => item.TokenHash == hash).Select(item => item.UsedAt).SingleAsync());
    }

    private static string NextPhone() => $"+7999{Interlocked.Increment(ref _phoneSequence):D7}";

    private sealed class RecordingCodeProvider : IVerificationCodeProvider
    {
        private readonly PhoneSuffixVerificationCodeProvider _inner = new();
        public int RequestCalls { get; private set; }
        public int VerifyCalls { get; private set; }

        public Task RequestCodeAsync(string phone, CancellationToken cancellationToken)
        {
            RequestCalls++;
            return _inner.RequestCodeAsync(phone, cancellationToken);
        }

        public Task<bool> VerifyCodeAsync(string phone, string? code, CancellationToken cancellationToken)
        {
            VerifyCalls++;
            return _inner.VerifyCodeAsync(phone, code, cancellationToken);
        }
    }
}
