// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;
using Sarafan.Core.StoreTests;

namespace Sarafan.Core.Tests;

[TestFixture, NonParallelizable]
public sealed class StoreApiTests
{
    private const string StaffPath = "/api/v1/backoffice/stores";
    private HttpClient _client = null!;

    [SetUp]
    public async Task Setup()
    {
        await IntegrationTestEnvironment.ResetAsync();
        _client = IntegrationTestEnvironment.Factory.CreateClient();
    }

    [TearDown] public void TearDown() => _client.Dispose();

    [Test]
    public async Task WebsiteOpsAndHttpWritesEnforceCurrentTldRules()
    {
        var token = await StaffToken(BackofficeRoles.Administrator);
        using var opsResponse = await Send(HttpMethod.Get, StaffPath + "/ops", token);
        var ops = (await opsResponse.Content.ReadFromJsonAsync<StoreOpsDto>())!;
        Assert.That(ops.OfficialUrlRules.MaximumLength, Is.EqualTo(ops.Limits.OfficialUrlMaxLength));
        Assert.That(ops.OfficialUrlRules.TopLevelDomains, Does.Contain("COM"));
        Assert.That(ops.OfficialUrlRules.TopLevelDomainListVersion, Is.Not.Empty);
        using var invalid = await Send(HttpMethod.Post, StaffPath, token, Form(url: "shop.invalid"));
        await Problem(invalid, HttpStatusCode.BadRequest, "invalid_store_url");
        using var invalidBody = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        Assert.That(invalidBody.RootElement.GetProperty("errors").TryGetProperty("officialUrl", out _), Is.True);
        using var creation = await Send(HttpMethod.Post, StaffPath, token, Form(url: "shop.example.com"));
        Assert.That(creation.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var store = (await creation.Content.ReadFromJsonAsync<StaffStoreDto>())!;
        Assert.That(store.OfficialUrl, Is.EqualTo("https://shop.example.com/"));
        using var badUpdate = await Send(HttpMethod.Put, $"{StaffPath}/{store.Id}", token, Form(store.Version, url: "127.0.0.1"));
        await Problem(badUpdate, HttpStatusCode.BadRequest, "invalid_store_url");
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.IanaTldCatalog.RemoveRange(database.IanaTldCatalog);
            await database.SaveChangesAsync();
        }
        using var unavailable = await Send(HttpMethod.Get, StaffPath + "/ops", token);
        await Problem(unavailable, HttpStatusCode.ServiceUnavailable, "tld_catalog_unavailable");
        using var readable = await Send(HttpMethod.Get, $"{StaffPath}/{store.Id}", token);
        Assert.That(readable.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task EveryStaffEndpointUsesTheExactRoleMatrix()
    {
        foreach (var role in BackofficeRoles.Codes)
        {
            var token = await StaffToken(role);
            var store = await Seed();
            foreach (var path in new[] { StaffPath, StaffPath + "?status=0", StaffPath + "/ops", $"{StaffPath}/{store.Id}", $"{StaffPath}/{store.Id}/logo" })
            {
                using var read = await Send(HttpMethod.Get, path, token);
                Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.OK), role + path);
                Assert.That(read.Headers.CacheControl!.NoStore, Is.True);
                if (path.EndsWith("/ops", StringComparison.Ordinal))
                {
                    var ops = (await read.Content.ReadFromJsonAsync<StoreOpsDto>())!;
                    Assert.That(ops.Actions, Is.EqualTo(new StoreActionsDto(true, role == BackofficeRoles.Administrator,
                        role is BackofficeRoles.Administrator or BackofficeRoles.ShiftManager, role == BackofficeRoles.Administrator)));
                    Assert.That(ops.Limits.LogoMaxBytes, Is.EqualTo(2 * 1024 * 1024));
                    Assert.That(ops.Limits.LogoMaxDimension, Is.EqualTo(4096));
                    Assert.That(ops.Limits.LogoMaxPixels, Is.EqualTo(4 * 1024 * 1024));
                    Assert.That(ops.Limits.LogoMaxFrames, Is.EqualTo(100));
                    Assert.That(ops.Limits.LogoMaxAnimationPixels, Is.EqualTo(16 * 1024 * 1024));
                    Assert.That(ops.Limits.LogoMaxMetadataBytes, Is.EqualTo(1024 * 1024));
                }
            }
            using var create = await Send(HttpMethod.Post, StaffPath, token, Form());
            Assert.That(create.StatusCode, Is.EqualTo(role == BackofficeRoles.Administrator ? HttpStatusCode.Created : HttpStatusCode.Forbidden));
            using var update = await Send(HttpMethod.Put, $"{StaffPath}/{store.Id}", token, Form(store.Version, displayOrder: store.DisplayOrder));
            var canEdit = role is BackofficeRoles.Administrator or BackofficeRoles.ShiftManager;
            Assert.That(update.StatusCode, Is.EqualTo(canEdit ? HttpStatusCode.OK : HttpStatusCode.Forbidden));
            var version = canEdit ? (await update.Content.ReadFromJsonAsync<StaffStoreDto>())!.Version : store.Version;
            using var delete = await Send(HttpMethod.Delete, $"{StaffPath}/{store.Id}", token, JsonContent.Create(new DeleteStoreRequest(version)));
            Assert.That(delete.StatusCode, Is.EqualTo(role == BackofficeRoles.Administrator ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden));
        }
    }

    [Test]
    public async Task StaffEndpointsRejectAnonymousAndCustomerIdentityForEveryAction()
    {
        using var scope = IntegrationTestEnvironment.Factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = new Customer { Phone = "+79990009999", Profile = new(), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        database.Customers.Add(customer);
        await database.SaveChangesAsync();
        var customerToken = scope.ServiceProvider.GetRequiredService<JwtTokenService>().CreateAccessToken(customer).Token;
        var store = await Seed();
        foreach (var token in new[] { null, customerToken })
        {
            foreach (var path in new[] { StaffPath, StaffPath + "/ops", $"{StaffPath}/{store.Id}", $"{StaffPath}/{store.Id}/logo" })
            {
                using var response = await Send(HttpMethod.Get, path, token);
                await Problem(response, HttpStatusCode.Unauthorized, "invalid_backoffice_access_token");
                Assert.That(response.Headers.WwwAuthenticate.Single().Scheme, Is.EqualTo("Bearer"));
            }
            using var create = await Send(HttpMethod.Post, StaffPath, token, Form());
            using var update = await Send(HttpMethod.Put, $"{StaffPath}/{store.Id}", token, Form(store.Version, displayOrder: store.DisplayOrder));
            using var delete = await Send(HttpMethod.Delete, $"{StaffPath}/{store.Id}", token, JsonContent.Create(new DeleteStoreRequest(store.Version)));
            foreach (var response in new[] { create, update, delete })
                await Problem(response, HttpStatusCode.Unauthorized, "invalid_backoffice_access_token");
        }
    }

    [Test]
    public async Task PriorityWireContractSeparatesCatalogueAndHomepageAndRequiresLogo()
    {
        var token = await StaffToken(BackofficeRoles.Administrator);
        using var opsResponse = await Send(HttpMethod.Get, StaffPath + "/ops", token);
        var ops = (await opsResponse.Content.ReadFromJsonAsync<StoreOpsDto>())!;
        Assert.That(ops.Limits.MaxPriorityStores, Is.EqualTo(6));
        Assert.That(ops.Statuses.Select(item => item.Name), Is.EqualTo(new[]
        {
            "Скрыт", "Показывается в общем списке", "Показывается в общем списке и на главной странице"
        }));
        Assert.That(ops.Statuses.Select(item => item.RouteAlias), Is.EqualTo(new[] { "hidden", "active", "priority" }));
        using var missingLogo = await Send(HttpMethod.Post, StaffPath, token, Form(status: StoreStatus.Priority, logo: false));
        await Problem(missingLogo, HttpStatusCode.BadRequest, "store_logo_required");
        using var activeResponse = await Send(HttpMethod.Post, StaffPath, token, Form(status: StoreStatus.Active, displayOrder: 0));
        using var priorityResponse = await Send(HttpMethod.Post, StaffPath, token, Form(status: StoreStatus.Priority, displayOrder: 1));
        using var hiddenResponse = await Send(HttpMethod.Post, StaffPath, token, Form(status: StoreStatus.Hidden, displayOrder: 2));
        foreach (var response in new[] { activeResponse, priorityResponse, hiddenResponse })
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var active = (await activeResponse.Content.ReadFromJsonAsync<StaffStoreDto>())!;
        var priority = (await priorityResponse.Content.ReadFromJsonAsync<StaffStoreDto>())!;
        Assert.That(priority.Status, Is.EqualTo(StoreStatus.Priority));
        Assert.That(await priorityResponse.Content.ReadAsStringAsync(), Does.Not.Contain("showOnHome"));
        var catalogue = (await _client.GetFromJsonAsync<StoreListDto<PublicStoreDto>>("/api/v1/stores"))!.Items;
        Assert.That(catalogue.Select(item => item.Id), Is.EqualTo(new[] { active.Id, priority.Id }));
        Assert.That((await _client.GetFromJsonAsync<StoreListDto<PublicStoreDto>>("/api/v1/stores/featured"))!.Items.Select(item => item.Id),
            Is.EqualTo(new[] { priority.Id }));
        using var staff = await Send(HttpMethod.Get, StaffPath + "?status=2", token);
        Assert.That((await staff.Content.ReadFromJsonAsync<StoreListDto<StaffStoreDto>>())!.Items.Select(item => item.Id), Is.EqualTo(new[] { priority.Id }));
        using var logo = await _client.GetAsync(catalogue.Single(item => item.Id == priority.Id).LogoUrl);
        Assert.That(logo.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task PublicReadsNeedNoConsentRatesOrTldAndLogoRevalidationChecksCurrentVisibility()
    {
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.ExchangeRateHistory.RemoveRange(database.ExchangeRateHistory);
            await database.SaveChangesAsync();
        }
        foreach (var path in new[] { "/api/v1/stores", "/api/v1/stores/featured" })
        {
            using var empty = await _client.GetAsync(path);
            Assert.That(empty.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await empty.Content.ReadFromJsonAsync<StoreListDto<PublicStoreDto>>())!.Items, Is.Empty);
            Assert.That(empty.Headers.CacheControl!.NoCache, Is.True);
        }
        var token = await StaffToken(BackofficeRoles.Administrator);
        using var creation = await Send(HttpMethod.Post, StaffPath, token, Form(status: StoreStatus.Active));
        Assert.That(creation.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var store = (await creation.Content.ReadFromJsonAsync<StaffStoreDto>())!;
        Assert.That(creation.Headers.Location!.ToString(), Does.EndWith($"{StaffPath}/{store.Id}"));
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.IanaTldCatalog.RemoveRange(database.IanaTldCatalog);
            await database.SaveChangesAsync();
        }
        using var catalogue = await _client.GetAsync("/api/v1/stores?sort=name-desc");
        var publicStore = (await catalogue.Content.ReadFromJsonAsync<StoreListDto<PublicStoreDto>>())!.Items.Single();
        var json = JsonDocument.Parse(await catalogue.Content.ReadAsStringAsync());
        Assert.That(json.RootElement.GetProperty("items")[0].EnumerateObject().Select(item => item.Name),
            Is.EquivalentTo(new[] { "id", "name", "description", "officialUrl", "logoUrl" }));
        using var logo = await _client.GetAsync(publicStore.LogoUrl);
        Assert.That(logo.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(logo.Content.Headers.ContentType!.MediaType, Is.EqualTo("image/png"));
        Assert.That(logo.Headers.GetValues("X-Content-Type-Options"), Does.Contain("nosniff"));
        Assert.That(logo.Headers.CacheControl!.NoCache, Is.True);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, publicStore.LogoUrl);
        conditional.Headers.IfNoneMatch.Add(logo.Headers.ETag!);
        using var unchanged = await _client.SendAsync(conditional);
        Assert.That(unchanged.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));

        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.IanaTldCatalog.Add(IntegrationTestEnvironment.CreateIanaTldCatalog());
            await database.SaveChangesAsync();
        }
        using var hiddenResponse = await Send(HttpMethod.Put, $"{StaffPath}/{store.Id}", token, Form(store.Version, logo: false));
        var hidden = (await hiddenResponse.Content.ReadFromJsonAsync<StaffStoreDto>())!;
        using var hiddenLogoRequest = new HttpRequestMessage(HttpMethod.Get, publicStore.LogoUrl);
        hiddenLogoRequest.Headers.IfNoneMatch.Add(logo.Headers.ETag!);
        using var hiddenLogo = await _client.SendAsync(hiddenLogoRequest);
        await Problem(hiddenLogo, HttpStatusCode.NotFound, "resource_not_found");
        Assert.That((await _client.GetFromJsonAsync<StoreListDto<PublicStoreDto>>("/api/v1/stores"))!.Items, Is.Empty);
        using var staffLogo = await Send(HttpMethod.Get, hidden.LogoUrl!, token);
        Assert.That(staffLogo.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var deleted = await Send(HttpMethod.Delete, $"{StaffPath}/{store.Id}", token, JsonContent.Create(new DeleteStoreRequest(hidden.Version)));
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using var missing = await _client.GetAsync(publicStore.LogoUrl);
        await Problem(missing, HttpStatusCode.NotFound, "resource_not_found");
    }

    [Test]
    public async Task HttpValidationAndConflictResponsesUseRussianProblemDetails()
    {
        foreach (var query in new[] { "?sort=", "?sort=unknown", "?sort=name-asc&sort=name-desc" })
        {
            using var response = await _client.GetAsync("/api/v1/stores" + query);
            await Problem(response, HttpStatusCode.BadRequest, "invalid_store_sort");
        }
        var token = await StaffToken(BackofficeRoles.Administrator);
        var store = await Seed();
        using var update = await Send(HttpMethod.Put, $"{StaffPath}/{store.Id}", token, Form(Guid.NewGuid()));
        await Problem(update, HttpStatusCode.Conflict, "store_update_conflict");
        using var delete = await Send(HttpMethod.Delete, $"{StaffPath}/{store.Id}", token, JsonContent.Create(new DeleteStoreRequest(Guid.NewGuid())));
        await Problem(delete, HttpStatusCode.Conflict, "store_update_conflict");
        using var noVersion = await Send(HttpMethod.Put, $"{StaffPath}/{store.Id}", token, Form());
        await Problem(noVersion, HttpStatusCode.BadRequest, "invalid_store_version");
        using var noLogo = await Send(HttpMethod.Post, StaffPath, token, Form(status: StoreStatus.Active, logo: false));
        await Problem(noLogo, HttpStatusCode.BadRequest, "store_logo_required");
        using var invalidName = Form(name: " ");
        using var invalid = await Send(HttpMethod.Post, StaffPath, token, invalidName);
        await Problem(invalid, HttpStatusCode.BadRequest, "invalid_store_name");
        using var malformed = Form(); malformed.Add(new StringContent("not-a-uuid"), "version");
        using var malformedResponse = await Send(HttpMethod.Put, $"{StaffPath}/{store.Id}", token, malformed);
        await Problem(malformedResponse, HttpStatusCode.BadRequest, "validation_failed");
        using var wrongMedia = await Send(HttpMethod.Post, StaffPath, token, JsonContent.Create(new { name = "Shop" }));
        await Problem(wrongMedia, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
    }

    [Test]
    public async Task MalformedUploadIsRejectedAndReplacementChangesPublicDigestWithoutChangingTheSchema()
    {
        var token = await StaffToken(BackofficeRoles.Administrator);
        using var malformed = Form(status: StoreStatus.Active, logo: false);
        var invalid = new ByteArrayContent([255, 216, 255, 217]);
        invalid.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        malformed.Add(invalid, "logo", "invalid.jpg");
        using var rejected = await Send(HttpMethod.Post, StaffPath, token, malformed);
        await Problem(rejected, HttpStatusCode.BadRequest, "invalid_store_logo_content");
        Assert.That((await _client.GetFromJsonAsync<StoreListDto<PublicStoreDto>>("/api/v1/stores"))!.Items, Is.Empty);

        using var creation = await Send(HttpMethod.Post, StaffPath, token, Form(status: StoreStatus.Active));
        var created = (await creation.Content.ReadFromJsonAsync<StaffStoreDto>())!;
        var before = (await _client.GetFromJsonAsync<StoreListDto<PublicStoreDto>>("/api/v1/stores"))!.Items.Single();
        using var previous = await _client.GetAsync(before.LogoUrl);
        using var replacement = Form(created.Version, StoreStatus.Active, logo: false);
        var webp = new ByteArrayContent(StoreImageFixtures.Webp);
        webp.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
        replacement.Add(webp, "logo", "logo.webp");
        using var saved = await Send(HttpMethod.Put, $"{StaffPath}/{created.Id}", token, replacement);
        Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var after = (await _client.GetFromJsonAsync<StoreListDto<PublicStoreDto>>("/api/v1/stores"))!.Items.Single();
        Assert.That(after.LogoUrl, Is.Not.EqualTo(before.LogoUrl));
        using var conditional = new HttpRequestMessage(HttpMethod.Get, before.LogoUrl);
        conditional.Headers.IfNoneMatch.Add(previous.Headers.ETag!);
        using var refreshed = await _client.SendAsync(conditional);
        Assert.That(refreshed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(refreshed.Content.Headers.ContentType!.MediaType, Is.EqualTo("image/webp"));
        Assert.That(await refreshed.Content.ReadAsByteArrayAsync(), Is.EqualTo(StoreImageFixtures.Webp));
    }

    internal static MultipartFormDataContent Form(Guid? version = null, StoreStatus status = StoreStatus.Hidden, bool logo = true, string name = "  Shop  ", int displayOrder = 3, string url = "https://shop.example.com")
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(name), "name" }, { new StringContent("Description"), "description" },
            { new StringContent(url), "officialUrl" },
            { new StringContent(((int)status).ToString()), "status" },
            { new StringContent(displayOrder.ToString()), "displayOrder" }
        };
        if (version.HasValue) form.Add(new StringContent(version.Value.ToString()), "version");
        if (logo)
        {
            var content = new ByteArrayContent(StoreServiceTests.Png);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(content, "logo", "logo.png");
        }
        return form;
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private static async Task<StaffStoreDto> Seed()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = StoreServiceTests.Request();
        request.DisplayOrder = (await database.Stores.Select(item => (int?)item.DisplayOrder).MaxAsync() ?? 3) + 1;
        return await scope.ServiceProvider.GetRequiredService<StoreService>()
            .CreateAsync(request, [BackofficeRoles.Administrator], default);
    }

    private static async Task<string> StaffToken(string role)
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new BackofficeUser
        {
            Email = $"{Guid.NewGuid()}@sarafan.test",
            NormalizedEmail = $"{Guid.NewGuid()}@sarafan.test",
            FirstName = "Stores",
            LastName = "Tests",
            PasswordHash = "not-used",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            UserRoles = [new BackofficeUserRole { RoleCode = role }]
        };
        database.BackofficeUsers.Add(user);
        await database.SaveChangesAsync();
        return scope.ServiceProvider.GetRequiredService<BackofficeJwtTokenService>().CreateAccessToken(user).Token;
    }

    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(status), body);
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/problem+json"));
        Assert.That(response.Content.Headers.ContentLanguage, Does.Contain("ru"));
        var problem = JsonSerializer.Deserialize<SarafanProblemDetails>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.That(problem.Code, Is.EqualTo(code));
        Assert.That(problem.Status, Is.EqualTo((int)status));
        Assert.That(problem.Type, Is.EqualTo("https://sarafan.sw.consulting/problems/" + code.Replace('_', '-')));
        Assert.That(problem.Instance, Does.StartWith("urn:sarafan:problem:"));
        Assert.That(problem.Title, Does.Match("[А-Яа-я]"));
        Assert.That(problem.Detail, Does.Match("[А-Яа-я]"));
    }
}
