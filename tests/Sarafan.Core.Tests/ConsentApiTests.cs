// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Tests;

[NonParallelizable]
public sealed class ConsentApiTests
{
    private HttpClient _client = null!;
    private string _adminToken = "";
    private string _customerToken = "";
    private int _customer;
    private int _admin;
    [SetUp]
    public async Task SetUp()
    {
        _client = IntegrationTestEnvironment.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = await db.BackofficeUsers.Include(x => x.UserRoles).FirstAsync(x => x.IsActive && x.UserRoles.Any(r => r.RoleCode == BackofficeRoles.Administrator));
        _admin = admin.Id;
        _adminToken = scope.ServiceProvider.GetRequiredService<BackofficeJwtTokenService>().CreateAccessToken(admin).Token;
        var customer = new Customer { Phone = "+7777" + Random.Shared.Next(1000000, 9999999), Profile = new() };
        db.Customers.Add(customer); await db.SaveChangesAsync(); _customer = customer.Id;
        _customerToken = scope.ServiceProvider.GetRequiredService<JwtTokenService>().CreateAccessToken(customer).Token;
    }
    [TearDown] public void TearDown() => _client.Dispose();
    private void Authorize(string? token) => _client.DefaultRequestHeaders.Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token);
    private static async Task<T> Read<T>(HttpResponseMessage response) { response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<T>())!; }
    private async Task<LegalDocumentDto> Current(string kind) => (await _client.GetFromJsonAsync<CurrentDocumentDto>("/api/v1/legal/current/" + kind))!.Document!;
    private static ConsentDecisionRequest Decision(LegalDocumentDto doc, string decision = "grant") => new()
    { DocumentId = doc.Id, ContentHash = doc.ContentHash, Decision = decision, IdempotencyKey = Guid.NewGuid() };

    [TestCase("personal-data-consent", "/api/v1/consents/me/personal-data")]
    [TestCase("cookie-consent", "/api/v1/consents/cookies")]
    public async Task OmittedDecisionCannotCreateConsent(string kind, string path)
    {
        Authorize(_customerToken);
        var document = await Current(kind);
        var key = Guid.NewGuid();
        using var response = await _client.PostAsJsonAsync(path, new { documentId = document.Id, contentHash = document.ContentHash, idempotencyKey = key, categories = Array.Empty<string>() });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        Assert.That(await scope.ServiceProvider.GetRequiredService<AppDbContext>().ConsentEvents.AnyAsync(x => x.IdempotencyKey == key), Is.False);
    }

    [Test]
    public async Task RegistrationValidatesConsentBeforePhoneNormalizationOrVerification()
    {
        var request = await ConsentTestData.Request(_client, "not-a-phone");
        var pd = request.PersonalDataConsent!;
        using var omitted = await _client.PostAsJsonAsync("/api/v1/auth/code/request", new
        {
            request.Phone,
            request.Purpose,
            request.TermsAccepted,
            request.TermsDocumentId,
            personalDataConsent = new { pd.DocumentId, pd.ContentHash, pd.IdempotencyKey }
        });
        Assert.That(omitted.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        request.PersonalDataConsent!.DocumentId = Guid.NewGuid();
        using var stale = await _client.PostAsJsonAsync("/api/v1/auth/code/request", request);
        Assert.That((await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(), Is.EqualTo("consent_version_changed"));
        request.PersonalDataConsent.DocumentId = (await Current("personal-data-consent")).Id;
        using var invalidPhone = await _client.PostAsJsonAsync("/api/v1/auth/code/request", request);
        Assert.That((await invalidPhone.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(), Is.EqualTo("invalid_phone"));
        using var invalidReceipt = await _client.PostAsJsonAsync("/api/v1/auth/code/verify", new { phone = "not-a-phone", purpose = "register", code = "0000", onboardingToken = "unknown" });
        Assert.That((await invalidReceipt.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(), Is.EqualTo("onboarding_consent_expired"));
    }

    [TestCase(null)]
    [TestCase("invalid")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    public async Task BrowserAssociationRejectsMissingOrInvalidAuthenticatedTokenId(string? tokenId)
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthenticationOptions>>().Value;
        var claims = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(_customerToken).Claims.Where(x => x.Type != "jti").ToList();
        if (tokenId is not null) claims.Add(new System.Security.Claims.Claim("jti", tokenId));
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(options.Issuer, options.Audience, claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new Microsoft.IdentityModel.Tokens.SigningCredentials(new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)), Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
        Authorize(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(jwt));
        using var response = await _client.PostAsync("/api/v1/consents/me/browser", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task AdministratorDocumentLifecycle_IsVersionedAndPublicOnlyAfterActivation()
    {
        using var denied = await _client.GetAsync("/api/v1/backoffice/legal-documents");
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Authorize(_customerToken);
        using var wrongPrincipal = await _client.GetAsync("/api/v1/backoffice/legal-documents");
        Assert.That(wrongPrincipal.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Authorize(_adminToken);
        var payload = new LegalDocumentRequest { Kind = "order-rules", Title = "Правила теста", DisplayVersion = Guid.NewGuid().ToString(), FileName = "rules.md", Source = Encoding.UTF8.GetBytes("# Правила\n\nТестовый текст.") };
        using var created = await _client.PostAsJsonAsync("/api/v1/backoffice/legal-documents", payload);
        var draft = await Read<LegalDocumentDto>(created);
        Assert.That(created.Headers.CacheControl!.NoStore, Is.True);
        payload.Revision = draft.Revision; payload.Title = "Исправленные правила";
        using var editedResponse = await _client.PutAsJsonAsync($"/api/v1/backoffice/legal-documents/{draft.Id}", payload);
        var edited = await Read<LegalDocumentDto>(editedResponse);
        using var staffRead = await _client.GetAsync($"/api/v1/backoffice/legal-documents/{draft.Id}");
        Assert.That((await Read<LegalDocumentDto>(staffRead)).Title, Is.EqualTo(payload.Title));
        using var source = await _client.GetAsync($"/api/v1/backoffice/legal-documents/{draft.Id}/source");
        Assert.That(await source.Content.ReadAsByteArrayAsync(), Is.EqualTo(payload.Source));
        Assert.That(source.Headers.GetValues("X-Content-Type-Options").Single(), Is.EqualTo("nosniff"));
        Assert.That(source.Content.Headers.ContentDisposition!.DispositionType, Is.EqualTo("attachment"));
        Authorize(null);
        using var hidden = await _client.GetAsync($"/api/v1/legal/documents/{draft.Id}");
        Assert.That(hidden.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Authorize(_adminToken);
        using var scheduledResponse = await _client.PostAsJsonAsync($"/api/v1/backoffice/legal-documents/{draft.Id}/publish", new { revision = edited.Revision, effectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)) });
        var scheduled = await Read<LegalDocumentDto>(scheduledResponse);
        Assert.That(scheduled.State, Is.EqualTo("scheduled"));
        using var cancelledResponse = await _client.PostAsJsonAsync($"/api/v1/backoffice/legal-documents/{draft.Id}/cancel", new { revision = scheduled.Revision });
        Assert.That((await Read<LegalDocumentDto>(cancelledResponse)).State, Is.EqualTo("cancelled"));
        payload.DisplayVersion = Guid.NewGuid().ToString();
        using var nextResponse = await _client.PostAsJsonAsync("/api/v1/backoffice/legal-documents", payload);
        var next = await Read<LegalDocumentDto>(nextResponse);
        using var publishedResponse = await _client.PostAsJsonAsync($"/api/v1/backoffice/legal-documents/{next.Id}/publish", new { revision = next.Revision, now = true });
        var published = await Read<LegalDocumentDto>(publishedResponse);
        Assert.That(published.State, Is.EqualTo("effective"));
        using var audit = await _client.GetAsync($"/api/v1/backoffice/legal-documents/{draft.Id}/audit");
        Assert.That(await Read<LegalAuditDto[]>(audit), Has.Length.EqualTo(4));
        using var list = await _client.GetAsync("/api/v1/backoffice/legal-documents?kind=order-rules");
        Assert.That((await Read<LegalDocumentDto[]>(list)).Any(x => x.Id == next.Id), Is.True);
        Authorize(null);
        using var publicRead = await _client.GetAsync($"/api/v1/legal/documents/{next.Id}");
        Assert.That((await Read<LegalDocumentDto>(publicRead)).CreatedBy, Is.Null);
        using var publicSource = await _client.GetAsync($"/api/v1/legal/documents/{next.Id}/source");
        Assert.That(await publicSource.Content.ReadAsByteArrayAsync(), Is.EqualTo(payload.Source));
    }
    [Test]
    public async Task CustomerGate_BrowserAssociation_RightsAndStaffQueue_WorkTogether()
    {
        var pd = await Current("personal-data-consent"); var cookies = await Current("cookie-consent");
        using var empty = await _client.GetAsync("/api/v1/consents/cookies");
        Assert.That(empty.Headers.Contains("Set-Cookie"), Is.False);
        Assert.That((await Read<CookieConsentDto>(empty)).Status, Is.EqualTo("missing"));
        var choice = Decision(cookies); choice.Categories = ["analytics"];
        using var cookieResponse = await _client.PostAsJsonAsync("/api/v1/consents/cookies", choice);
        Assert.That((await Read<CookieConsentDto>(cookieResponse)).Categories, Is.EqualTo(new[] { "analytics" }));
        var setCookie = cookieResponse.Headers.GetValues("Set-Cookie").Single();
        Assert.That(setCookie, Does.Contain("httponly").IgnoreCase.And.Contain("samesite=strict").IgnoreCase.And.Contain("path=/api/v1/consents"));
        _client.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';')[0]);
        using var retry = await _client.PostAsJsonAsync("/api/v1/consents/cookies", choice);
        Assert.That((await Read<CookieConsentDto>(retry)).Status, Is.EqualTo("current"));
        Authorize(_customerToken);
        using var gated = await _client.PutAsJsonAsync("/api/v1/customers/me", new CustomerProfileUpdateRequest { FirstName = "Тест" });
        Assert.That(gated.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        // Consent is checked before multipart binding can buffer unconsented personal data.
        using var malformedPhoto = new StringContent("not a multipart body");
        malformedPhoto.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("multipart/form-data");
        using var gatedPhoto = await _client.PutAsync("/api/v1/customers/me/photo", malformedPhoto);
        Assert.That(gatedPhoto.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var mineBefore = await _client.GetAsync("/api/v1/consents/me");
        Assert.That((await Read<CustomerConsentsDto>(mineBefore)).Statuses.Single().Status, Is.EqualTo("missing"));
        using var link = await _client.PostAsync("/api/v1/consents/me/browser", null);
        Assert.That(link.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var association = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ConsentAssociations.SingleAsync(x => x.CustomerId == _customer);
            var tokenId = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(_customerToken).Id;
            Assert.That(association.AuthenticationTokenId, Is.EqualTo(Guid.Parse(tokenId)));
        }
        var stale = Decision(pd); stale.DocumentId = Guid.NewGuid();
        using var conflict = await _client.PostAsJsonAsync("/api/v1/consents/me/personal-data", stale);
        Assert.That(conflict.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var error = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(error.GetProperty("requiredDocumentId").GetGuid(), Is.EqualTo(pd.Id));
        using var grant = await _client.PostAsJsonAsync("/api/v1/consents/me/personal-data", Decision(pd));
        var mine = await Read<CustomerConsentsDto>(grant);
        Assert.That(mine.Statuses.Single().Status, Is.EqualTo("current"));
        Assert.That(mine.History.Any(x => x.Scope == "observed-browser"), Is.True);
        using var write = await _client.PutAsJsonAsync("/api/v1/customers/me", new CustomerProfileUpdateRequest { FirstName = "Тест" });
        Assert.That(write.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var rightsResponse = await _client.PostAsJsonAsync("/api/v1/consents/me/rights", new RightsRequest { IdempotencyKey = Guid.NewGuid() });
        var rights = await Read<RightsCaseDto>(rightsResponse);
        using var blocked = await _client.PutAsJsonAsync("/api/v1/customers/me", new CustomerProfileUpdateRequest { FirstName = "Не сохранять" });
        Assert.That(blocked.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var own = await _client.GetAsync("/api/v1/consents/me");
        Assert.That((await Read<CustomerConsentsDto>(own)).RightsCases.Single().Id, Is.EqualTo(rights.Id));
        Authorize(_adminToken);
        using var staffMine = await _client.GetAsync($"/api/v1/backoffice/consents/customers/{_customer}");
        Assert.That((await Read<CustomerConsentsDto>(staffMine)).Statuses.Single().Status, Is.EqualTo("withdrawn"));
        using var queue = await _client.GetAsync($"/api/v1/backoffice/consents/rights?customerId={_customer}");
        Assert.That(await Read<RightsCaseDto[]>(queue), Has.Length.EqualTo(1));
        using var completed = await _client.PutAsJsonAsync($"/api/v1/backoffice/consents/rights/{rights.Id}", new RightsCaseUpdate { Revision = rights.Revision, State = "completed", ResponsibleStaffId = _admin, CompletionEvidence = "Тест: обработчики подтвердили прекращение" });
        Assert.That((await Read<RightsCaseDto>(completed)).CompletedAt, Is.Not.Null);
        using var customerApiDenied = await _client.GetAsync("/api/v1/consents/me");
        Assert.That(customerApiDenied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
    [TestCase(BackofficeRoles.Operator)]
    [TestCase(BackofficeRoles.SeniorOperator)]
    [TestCase(BackofficeRoles.ShiftManager)]
    public async Task NonAdministrators_CannotReadOrManageConsentEvidence(string role)
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new BackofficeUser { Email = $"{Guid.NewGuid():N}@consent.test", NormalizedEmail = $"{Guid.NewGuid():N}@consent.test", FirstName = "Тест", LastName = "Тест", PasswordHash = "not-a-real-password", IsActive = true };
        user.UserRoles.Add(new() { RoleCode = role }); db.BackofficeUsers.Add(user); await db.SaveChangesAsync();
        Authorize(scope.ServiceProvider.GetRequiredService<BackofficeJwtTokenService>().CreateAccessToken(user).Token);
        foreach (var path in new[] { "/api/v1/backoffice/legal-documents", $"/api/v1/backoffice/consents/customers/{_customer}", "/api/v1/backoffice/consents/rights" })
        {
            using var response = await _client.GetAsync(path);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }
        using var mutation = await _client.PostAsJsonAsync("/api/v1/backoffice/legal-documents", new LegalDocumentRequest());
        Assert.That(mutation.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }
}
