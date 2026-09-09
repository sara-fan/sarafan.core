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
        var cookieDocument = await Current(LegalDocumentKind.CookieConsent);
        using var cookieResponse = await _client.PostAsJsonAsync("/api/v1/consents/cookies", Decision(cookieDocument));
        cookieResponse.EnsureSuccessStatusCode();
        _client.DefaultRequestHeaders.Add("Cookie", cookieResponse.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
    }
    [TearDown] public void TearDown() => _client.Dispose();
    private void Authorize(string? token) => _client.DefaultRequestHeaders.Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token);
    private static async Task<T> Read<T>(HttpResponseMessage response) { response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<T>())!; }
    private async Task<LegalDocumentDto> Current(LegalDocumentKind kind) => (await _client.GetFromJsonAsync<CurrentDocumentDto>("/api/v1/legal/current/" + (int)kind))!.Document!;
    private static ConsentDecisionRequest Decision(LegalDocumentDto doc, string decision = "grant") => new()
    {
        DocumentId = doc.Id,
        ContentHash = doc.ContentHash,
        Decision = decision,
        Categories = decision == "grant" && doc.Kind == LegalDocumentKind.CookieConsent ? [CookieCategory.Mandatory] : [],
        IdempotencyKey = Guid.NewGuid()
    };

    [Test]
    public async Task LegalDocumentOperationsAreIdenticalPublicMetadataAndStaffAuthorized()
    {
        using var publicResponse = await _client.GetAsync("/api/v1/legal/ops");
        var publicOps = await Read<LegalDocumentOpsDto>(publicResponse);
        Assert.That(publicOps.Kinds.Select(item => item.Value), Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
        Assert.That(publicOps.Kinds.Select(item => item.Name), Does.Contain("Согласие на использование куки"));
        Assert.That(publicOps.Kinds.Select(item => item.RouteAlias), Does.Contain("privacy-policy"));
        Assert.That(publicOps.CookieCategories, Is.EqualTo(new[] { new CookieCategoryOpsItemDto(0, "Обязательные", true) }));

        using var anonymousStaff = await _client.GetAsync("/api/v1/backoffice/legal-documents/ops");
        Assert.That(anonymousStaff.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Authorize(_customerToken);
        using var customerStaff = await _client.GetAsync("/api/v1/backoffice/legal-documents/ops");
        Assert.That(customerStaff.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Authorize(_adminToken);
        using var staffResponse = await _client.GetAsync("/api/v1/backoffice/legal-documents/ops");
        var staffOps = await Read<LegalDocumentOpsDto>(staffResponse);
        Assert.That(staffOps.Kinds, Is.EqualTo(publicOps.Kinds));
        Assert.That(staffOps.CookieCategories, Is.EqualTo(publicOps.CookieCategories));
    }

    [Test]
    public async Task LegalDocumentKindsUseOnlyDefinedNumericContracts()
    {
        using var numeric = await _client.GetAsync($"/api/v1/legal/current/{(int)LegalDocumentKind.CookieConsent}");
        Assert.That((await Read<CurrentDocumentDto>(numeric)).Document!.Kind, Is.EqualTo(LegalDocumentKind.CookieConsent));
        using var legacyAlias = await _client.GetAsync("/api/v1/legal/current/cookie-consent");
        Assert.That(legacyAlias.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using var undefinedRoute = await _client.GetAsync("/api/v1/legal/current/99");
        Assert.That((await undefinedRoute.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(), Is.EqualTo("validation_failed"));

        Authorize(_adminToken);
        using var undefinedFilter = await _client.GetAsync("/api/v1/backoffice/legal-documents?kind=99");
        Assert.That((await undefinedFilter.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(), Is.EqualTo("validation_failed"));
        using var undefinedAuditFilter = await _client.GetAsync("/api/v1/backoffice/legal-documents/audit?kind=99");
        Assert.That((await undefinedAuditFilter.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(), Is.EqualTo("validation_failed"));
        using var missingBody = await _client.PostAsJsonAsync("/api/v1/backoffice/legal-documents/preview", new
        {
            locale = "ru",
            title = "Документ",
            displayVersion = Guid.NewGuid().ToString(),
            fileName = "document.md",
            source = Encoding.UTF8.GetBytes("Текст"),
            effectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1))
        });
        Assert.That((await missingBody.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(), Is.EqualTo("validation_failed"));
        using var undefinedBody = await _client.PostAsJsonAsync("/api/v1/backoffice/legal-documents/preview", new LegalDocumentRequest
        {
            Kind = (LegalDocumentKind)99,
            Title = "Документ",
            DisplayVersion = Guid.NewGuid().ToString(),
            FileName = "document.md",
            Source = Encoding.UTF8.GetBytes("Текст"),
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1))
        });
        Assert.That((await undefinedBody.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(), Is.EqualTo("invalid_legal_document_kind"));
        using var unsafeMarkup = await _client.PostAsJsonAsync("/api/v1/backoffice/legal-documents/preview", new LegalDocumentPreviewRequest
        {
            Kind = LegalDocumentKind.PrivacyPolicy,
            Title = "Политика",
            FileName = "policy.md",
            Source = Encoding.UTF8.GetBytes("<script>alert(1)</script>"),
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1))
        });
        Assert.That((await unsafeMarkup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(), Is.EqualTo("legal_document_html_not_allowed"));
        using var stringBody = new StringContent("{\"kind\":\"cookie-consent\"}", Encoding.UTF8, "application/json");
        using var rejectedString = await _client.PostAsync("/api/v1/backoffice/legal-documents/preview", stringBody);
        Assert.That((await rejectedString.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(), Is.EqualTo("validation_failed"));
    }

    [TestCase(LegalDocumentKind.PersonalDataConsent, "/api/v1/consents/me/personal-data")]
    [TestCase(LegalDocumentKind.CookieConsent, "/api/v1/consents/cookies")]
    public async Task OmittedDecisionCannotCreateConsent(LegalDocumentKind kind, string path)
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
        request.PersonalDataConsent.DocumentId = (await Current(LegalDocumentKind.PersonalDataConsent)).Id;
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
        var challenge = response.Headers.WwwAuthenticate.Single();
        Assert.That(challenge.Scheme, Is.EqualTo("Bearer"));
        Assert.That(challenge.Parameter, Is.EqualTo("error=\"invalid_token\""));
        Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(), Is.EqualTo("invalid_access_token"));
    }

    [Test]
    public async Task AdministratorCreatesPreviewsDeletesAndAuditsImmutableDocuments()
    {
        using var denied = await _client.GetAsync("/api/v1/backoffice/legal-documents");
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var deniedAudit = await _client.GetAsync("/api/v1/backoffice/legal-documents/audit");
        Assert.That(deniedAudit.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Authorize(_customerToken);
        using var wrongPrincipal = await _client.GetAsync("/api/v1/backoffice/legal-documents");
        Assert.That(wrongPrincipal.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var wrongAuditPrincipal = await _client.GetAsync("/api/v1/backoffice/legal-documents/audit");
        Assert.That(wrongAuditPrincipal.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Authorize(_adminToken);
        var payload = new LegalDocumentRequest
        {
            Kind = LegalDocumentKind.OrderRules,
            Title = "Правила теста",
            DisplayVersion = Guid.NewGuid().ToString(),
            FileName = "rules.md",
            Source = Encoding.UTF8.GetBytes("# Правила\n\nТестовый текст."),
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10))
        };
        var displayVersion = payload.DisplayVersion;
        payload.DisplayVersion = "";
        using var versionlessPreviewResponse = await _client.PostAsJsonAsync("/api/v1/backoffice/legal-documents/preview", payload);
        Assert.That((await Read<LegalDocumentPreviewDto>(versionlessPreviewResponse)).Html, Is.Not.Empty);
        using var versionlessCreation = await _client.PostAsJsonAsync("/api/v1/backoffice/legal-documents", payload);
        Assert.That(versionlessCreation.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await versionlessCreation.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(),
            Is.EqualTo("validation_failed"));
        payload.DisplayVersion = displayVersion;
        using var previewResponse = await _client.PostAsJsonAsync("/api/v1/backoffice/legal-documents/preview", payload);
        var preview = await Read<JsonElement>(previewResponse);
        Assert.That(preview.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "html" }));
        using var created = await _client.PostAsJsonAsync("/api/v1/backoffice/legal-documents", payload);
        var document = await Read<LegalDocumentDto>(created);
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(created.Headers.CacheControl!.NoStore, Is.True);
        Assert.That(document.Html, Is.EqualTo(preview.GetProperty("html").GetString()));
        Assert.That(document.ContentHash, Has.Length.EqualTo(64));
        Assert.That(document.CanDelete, Is.True);
        using var staffRead = await _client.GetAsync($"/api/v1/backoffice/legal-documents/{document.Id}");
        Assert.That((await Read<LegalDocumentDto>(staffRead)).Title, Is.EqualTo(payload.Title));
        using var source = await _client.GetAsync($"/api/v1/backoffice/legal-documents/{document.Id}/source");
        Assert.That(await source.Content.ReadAsByteArrayAsync(), Is.EqualTo(payload.Source));
        Assert.That(source.Headers.GetValues("X-Content-Type-Options").Single(), Is.EqualTo("nosniff"));
        Assert.That(source.Content.Headers.ContentDisposition!.DispositionType, Is.EqualTo("attachment"));
        Authorize(null);
        using var hidden = await _client.GetAsync($"/api/v1/legal/documents/{document.Id}");
        Assert.That(hidden.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Authorize(_adminToken);
        using var legacyEdit = await _client.PutAsJsonAsync($"/api/v1/backoffice/legal-documents/{document.Id}", payload);
        Assert.That(legacyEdit.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
        using var legacyPublish = await _client.PostAsync($"/api/v1/backoffice/legal-documents/{document.Id}/publish", null);
        using var legacyCancel = await _client.PostAsync($"/api/v1/backoffice/legal-documents/{document.Id}/cancel", null);
        Assert.That(legacyPublish.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(legacyCancel.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        using var auditBeforeDelete = await _client.GetAsync($"/api/v1/backoffice/legal-documents/audit?documentId={document.Id}");
        var createdAudit = await Read<LegalDocumentAuditPageDto>(auditBeforeDelete);
        Assert.That(createdAudit.Items.Single().Action, Is.EqualTo("created"));
        Assert.That(createdAudit.Items.Single().ContentHash, Is.EqualTo(document.ContentHash));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(createdAudit.Pagination.CurrentPage, Is.EqualTo(1));
            Assert.That(createdAudit.Pagination.PageSize, Is.EqualTo(50));
            Assert.That(createdAudit.Pagination.TotalCount, Is.EqualTo(1));
            Assert.That(createdAudit.Pagination.TotalPages, Is.EqualTo(1));
            Assert.That(createdAudit.Pagination.HasNextPage, Is.False);
            Assert.That(createdAudit.Pagination.HasPreviousPage, Is.False);
            Assert.That(createdAudit.Sorting.SortBy, Is.EqualTo("at"));
            Assert.That(createdAudit.Sorting.SortOrder, Is.EqualTo("desc"));
            Assert.That(createdAudit.Search, Is.Null);
        }
        using var searchedAudit = await _client.GetAsync($"/api/v1/backoffice/legal-documents/audit?search={Uri.EscapeDataString(payload.DisplayVersion)}&action=created&page=1&pageSize=1");
        var searchedAuditPage = await Read<LegalDocumentAuditPageDto>(searchedAudit);
        Assert.That(searchedAuditPage.Items.Single().DocumentId, Is.EqualTo(document.Id));
        Assert.That(searchedAuditPage.Search, Is.EqualTo(payload.DisplayVersion));
        using var sortedAudit = await _client.GetAsync($"/api/v1/backoffice/legal-documents/audit?documentId={document.Id}&sortBy=title&sortOrder=asc");
        var sortedAuditPage = await Read<LegalDocumentAuditPageDto>(sortedAudit);
        Assert.That(sortedAuditPage.Sorting.SortBy, Is.EqualTo("title"));
        Assert.That(sortedAuditPage.Sorting.SortOrder, Is.EqualTo("asc"));
        using var invalidAudit = await _client.GetAsync("/api/v1/backoffice/legal-documents/audit?pageSize=101");
        Assert.That((await invalidAudit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(),
            Is.EqualTo("invalid_legal_document_audit_filter"));
        using var deleted = await _client.DeleteAsync($"/api/v1/backoffice/legal-documents/{document.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using var auditAfterDelete = await _client.GetAsync($"/api/v1/backoffice/legal-documents/audit?documentId={document.Id}");
        Assert.That((await Read<LegalDocumentAuditPageDto>(auditAfterDelete)).Items.Select(x => x.Action), Is.EqualTo(new[] { "deleted", "created" }));
        using var list = await _client.GetAsync($"/api/v1/backoffice/legal-documents?kind={(int)LegalDocumentKind.OrderRules}");
        Assert.That((await Read<LegalDocumentDto[]>(list)).Any(x => x.Id == document.Id), Is.False);
        Authorize(null);
        var current = await Current(LegalDocumentKind.OrderRules);
        Authorize(_adminToken);
        using var effectiveDelete = await _client.DeleteAsync($"/api/v1/backoffice/legal-documents/{current.Id}");
        Assert.That(effectiveDelete.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That((await effectiveDelete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(), Is.EqualTo("legal_document_already_effective"));
        Authorize(null);
        using var publicRead = await _client.GetAsync($"/api/v1/legal/documents/{current.Id}");
        Assert.That((await Read<LegalDocumentDto>(publicRead)).CreatedBy, Is.Null);
        using var publicSource = await _client.GetAsync($"/api/v1/legal/documents/{current.Id}/source");
        Assert.That(await publicSource.Content.ReadAsByteArrayAsync(), Is.Not.Empty);
    }
    [Test]
    public async Task CustomerGate_BrowserAssociation_AndManualWithdrawalQueue_WorkTogether()
    {
        _client.DefaultRequestHeaders.Remove("Cookie");
        var pd = await Current(LegalDocumentKind.PersonalDataConsent); var cookies = await Current(LegalDocumentKind.CookieConsent);
        using var empty = await _client.GetAsync("/api/v1/consents/cookies");
        Assert.That(empty.Headers.Contains("Set-Cookie"), Is.False);
        Assert.That((await Read<CookieConsentDto>(empty)).Status, Is.EqualTo("missing"));
        using var blockedAuthentication = await _client.PostAsJsonAsync("/api/v1/auth/code/request", new { });
        Assert.That(blockedAuthentication.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That((await blockedAuthentication.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(), Is.EqualTo("cookie_consent_required"));
        Authorize(_customerToken);
        using var blockedService = await _client.GetAsync("/api/v1/customers/me");
        Assert.That(blockedService.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        using var allowedHistory = await _client.GetAsync("/api/v1/consents/me");
        Assert.That(allowedHistory.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Authorize(null);
        var choice = Decision(cookies); choice.Categories = [CookieCategory.Mandatory];
        using var cookieResponse = await _client.PostAsJsonAsync("/api/v1/consents/cookies", choice);
        Assert.That((await Read<CookieConsentDto>(cookieResponse)).Categories, Is.EqualTo(new[] { CookieCategory.Mandatory }));
        var setCookie = cookieResponse.Headers.GetValues("Set-Cookie").Single();
        Assert.That(setCookie, Does.Contain("httponly").IgnoreCase.And.Contain("samesite=strict").IgnoreCase.And.Contain("path=/api/v1"));
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
        Assert.That(error.GetProperty("consentKind").GetInt32(), Is.EqualTo((int)LegalDocumentKind.PersonalDataConsent));
        using var grant = await _client.PostAsJsonAsync("/api/v1/consents/me/personal-data", Decision(pd));
        var mine = await Read<CustomerConsentsDto>(grant);
        Assert.That(mine.Statuses.Single().Status, Is.EqualTo("current"));
        Assert.That(mine.History.Any(x => x.Scope == "observed-browser"), Is.True);
        using var write = await _client.PutAsJsonAsync("/api/v1/customers/me", new CustomerProfileUpdateRequest { FirstName = "Тест" });
        Assert.That(write.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var requestResponse = await _client.PostAsync("/api/v1/consents/me/withdrawal-request", null);
        var request = await Read<CustomerConsentWithdrawalRequestDto>(requestResponse);
        Assert.That(request.Processed, Is.False);
        using var stillAllowed = await _client.PutAsJsonAsync("/api/v1/customers/me", new CustomerProfileUpdateRequest { FirstName = "Сохранять" });
        Assert.That(stillAllowed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var own = await _client.GetAsync("/api/v1/consents/me");
        var ownConsents = await Read<CustomerConsentsDto>(own);
        Assert.That(ownConsents.WithdrawalRequest, Is.EqualTo(request));
        Assert.That(ownConsents.Statuses.Single().Status, Is.EqualTo("current"));
        Assert.That(ownConsents.History, Has.None.Property(nameof(ConsentHistoryDto.Decision)).EqualTo("withdraw"));
        Authorize(null);
        using var anonymousQueue = await _client.GetAsync("/api/v1/backoffice/consents/withdrawal-requests");
        Assert.That(anonymousQueue.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Authorize(_customerToken);
        using var customerQueue = await _client.GetAsync("/api/v1/backoffice/consents/withdrawal-requests");
        Assert.That(customerQueue.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Authorize(_adminToken);
        using var queue = await _client.GetAsync("/api/v1/backoffice/consents/withdrawal-requests");
        var queuePage = await Read<CustomerConsentWithdrawalRequestPageDto>(queue);
        Assert.That(queuePage.Items.Any(item => item == request), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuePage.Pagination.CurrentPage, Is.EqualTo(1));
            Assert.That(queuePage.Pagination.PageSize, Is.EqualTo(10));
            Assert.That(queuePage.Pagination.TotalCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(queuePage.Sorting.SortBy, Is.EqualTo("processed"));
            Assert.That(queuePage.Sorting.SortOrder, Is.EqualTo("asc"));
            Assert.That(queuePage.Search, Is.Null);
        }
        var customerSearch = request.CustomerId.ToString()[^1].ToString();
        using var filteredQueue = await _client.GetAsync($"/api/v1/backoffice/consents/withdrawal-requests?page=1&pageSize=10&sortBy=customerId&sortOrder=desc&processed=false&search={customerSearch}");
        var filteredQueuePage = await Read<CustomerConsentWithdrawalRequestPageDto>(filteredQueue);
        Assert.That(filteredQueuePage.Items, Has.Some.EqualTo(request));
        Assert.That(filteredQueuePage.Items, Has.All.Property(nameof(CustomerConsentWithdrawalRequestDto.Processed)).False);
        Assert.That(filteredQueuePage.Sorting.SortBy, Is.EqualTo("customerId"));
        Assert.That(filteredQueuePage.Search, Is.EqualTo(customerSearch));
        using var invalidQueue = await _client.GetAsync("/api/v1/backoffice/consents/withdrawal-requests?search=customer");
        Assert.That((await invalidQueue.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(),
            Is.EqualTo("invalid_consent_withdrawal_request_filter"));
        using var completed = await _client.PutAsJsonAsync("/api/v1/backoffice/consents/withdrawal-requests/processed",
            new ProcessConsentWithdrawalRequest { CustomerId = request.CustomerId, RequestedAt = request.RequestedAt });
        Assert.That((await Read<CustomerConsentWithdrawalRequestDto>(completed)).Processed, Is.True);
        using var missing = await _client.PutAsJsonAsync("/api/v1/backoffice/consents/withdrawal-requests/processed",
            new ProcessConsentWithdrawalRequest { CustomerId = int.MaxValue, RequestedAt = request.RequestedAt });
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(), Is.EqualTo("consent_withdrawal_request_not_found"));
        Authorize(_customerToken);
        using var afterProcessing = await _client.GetAsync("/api/v1/consents/me");
        Assert.That((await Read<CustomerConsentsDto>(afterProcessing)).WithdrawalRequest!.Processed, Is.True);
    }
    [Test]
    public async Task ConcurrentWithdrawalRequestsCreateOnePendingRecord()
    {
        Authorize(_customerToken);
        var responses = await Task.WhenAll(
            _client.PostAsync("/api/v1/consents/me/withdrawal-request", null),
            _client.PostAsync("/api/v1/consents/me/withdrawal-request", null));
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];
        var first = await Read<CustomerConsentWithdrawalRequestDto>(firstResponse);
        var second = await Read<CustomerConsentWithdrawalRequestDto>(secondResponse);
        Assert.That(second, Is.EqualTo(first));

        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.That(await database.CustomerConsentWithdrawalRequests
            .CountAsync(item => item.CustomerId == _customer && !item.Processed), Is.EqualTo(1));
    }

    [Test]
    public async Task WithdrawalRequestRetryAndResubmissionDoNotChangeConsentEvidence()
    {
        Authorize(_customerToken);
        var document = await Current(LegalDocumentKind.PersonalDataConsent);
        using var initial = await _client.PostAsJsonAsync("/api/v1/consents/me/personal-data", Decision(document));
        initial.EnsureSuccessStatusCode();
        using var firstResponse = await _client.PostAsync("/api/v1/consents/me/withdrawal-request", null);
        var first = await Read<CustomerConsentWithdrawalRequestDto>(firstResponse);
        using var retryResponse = await _client.PostAsync("/api/v1/consents/me/withdrawal-request", null);
        Assert.That(await Read<CustomerConsentWithdrawalRequestDto>(retryResponse), Is.EqualTo(first));
        Authorize(_adminToken);
        using var completedResponse = await _client.PutAsJsonAsync("/api/v1/backoffice/consents/withdrawal-requests/processed",
            new ProcessConsentWithdrawalRequest { CustomerId = first.CustomerId, RequestedAt = first.RequestedAt });
        completedResponse.EnsureSuccessStatusCode();
        Authorize(_customerToken);
        using var secondResponse = await _client.PostAsync("/api/v1/consents/me/withdrawal-request", null);
        var second = await Read<CustomerConsentWithdrawalRequestDto>(secondResponse);
        Assert.That(second.RequestedAt, Is.GreaterThanOrEqualTo(first.RequestedAt));
        Assert.That(second.Processed, Is.False);
        using var mineResponse = await _client.GetAsync("/api/v1/consents/me");
        var consents = await Read<CustomerConsentsDto>(mineResponse);
        Assert.That(consents.Statuses.Single().Status, Is.EqualTo("current"));
        Assert.That(consents.History.Count(x => x.Kind == LegalDocumentKind.PersonalDataConsent && x.Decision == "grant"), Is.EqualTo(1));
        Assert.That(consents.History, Has.None.Property(nameof(ConsentHistoryDto.Decision)).EqualTo("withdraw"));
        Assert.That(consents.WithdrawalRequest, Is.EqualTo(second));
        using var allowed = await _client.PutAsJsonAsync("/api/v1/customers/me", new CustomerProfileUpdateRequest { FirstName = "Recovered" });
        Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [TestCase(BackofficeRoles.Operator, false)]
    [TestCase(BackofficeRoles.SeniorOperator, true)]
    [TestCase(BackofficeRoles.ShiftManager, true)]
    public async Task StaffAuthorizationSeparatesLegalDocumentsFromWithdrawalQueue(string role, bool canManageQueue)
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new BackofficeUser { Email = $"{Guid.NewGuid():N}@consent.test", NormalizedEmail = $"{Guid.NewGuid():N}@consent.test", FirstName = "Тест", LastName = "Тест", PasswordHash = "not-a-real-password", IsActive = true };
        user.UserRoles.Add(new() { RoleCode = role }); db.BackofficeUsers.Add(user); await db.SaveChangesAsync();
        Authorize(scope.ServiceProvider.GetRequiredService<BackofficeJwtTokenService>().CreateAccessToken(user).Token);
        using var legalDocuments = await _client.GetAsync("/api/v1/backoffice/legal-documents");
        Assert.That(legalDocuments.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        using var queue = await _client.GetAsync("/api/v1/backoffice/consents/withdrawal-requests");
        Assert.That(queue.StatusCode, Is.EqualTo(canManageQueue ? HttpStatusCode.OK : HttpStatusCode.Forbidden));
        using var mutation = await _client.PostAsJsonAsync("/api/v1/backoffice/legal-documents", new LegalDocumentRequest());
        Assert.That(mutation.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }
}
