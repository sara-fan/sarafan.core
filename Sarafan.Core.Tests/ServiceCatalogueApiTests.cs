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

namespace Sarafan.Core.Tests;

[TestFixture, NonParallelizable]
public sealed class ServiceCatalogueApiTests
{
    private const string Path = "/api/v1/backoffice/service-catalogue";
    private HttpClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        await IntegrationTestEnvironment.ResetAsync();
        _client = IntegrationTestEnvironment.Factory.CreateClient();
    }

    [TearDown]
    public void TearDown() => _client.Dispose();

    [Test]
    public async Task OpsAndReadsAllowEveryStaffRoleWhileMutationsRequireAdministrator()
    {
        foreach (var role in BackofficeRoles.Codes)
        {
            var token = await StaffToken(role);
            using var opsResponse = await Send(HttpMethod.Get, Path + "/ops", token);
            using var listResponse = await Send(HttpMethod.Get, Path, token);
            using var auditResponse = await Send(HttpMethod.Get, Path + "/audit?page=1&pageSize=10&sortBy=timestamp&sortOrder=desc", token);
            Assert.That(new[] { opsResponse.StatusCode, listResponse.StatusCode, auditResponse.StatusCode },
                Is.All.EqualTo(HttpStatusCode.OK), role);
            Assert.That(new[] { opsResponse, listResponse, auditResponse },
                Is.All.Matches<HttpResponseMessage>(response => response.Headers.CacheControl!.NoStore));
            var ops = (await opsResponse.Content.ReadFromJsonAsync<ServiceCatalogueOpsDto>())!;
            Assert.That(ops.Currencies.Select(item => item.Value), Is.EqualTo(new[] { 643, 840 }));
            Assert.That(ops.Currencies.Select(item => item.Symbol), Is.EqualTo(new[] { "₽", "$" }));
            Assert.That(ops.Actions.Create, Is.EqualTo(role == BackofficeRoles.Administrator));

            using var mutation = await Send(HttpMethod.Post, Path, token, JsonContent.Create(Manual()));
            Assert.That(mutation.StatusCode,
                Is.EqualTo(role == BackofficeRoles.Administrator ? HttpStatusCode.Created : HttpStatusCode.Forbidden), role);
        }
    }

    [Test]
    public async Task CrudAuditAndConflictContractsAreStableAndTyped()
    {
        var token = await StaffToken(BackofficeRoles.Administrator);
        var createRequest = Percent();
        createRequest.AvailableBy = new DateOnly(2026, 2, 28);
        using var creation = await Send(HttpMethod.Post, Path, token, JsonContent.Create(createRequest));
        Assert.That(creation.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var created = (await creation.Content.ReadFromJsonAsync<ServiceCatalogueEntryDto>())!;
        Assert.That(creation.Headers.Location!.ToString(), Does.EndWith($"{Path}/{created.Id}"));

        using var get = await Send(HttpMethod.Get, $"{Path}/{created.Id}", token);
        Assert.That((await get.Content.ReadFromJsonAsync<ServiceCatalogueEntryDto>())!.Percentage, Is.EqualTo(10.125m));
        var updateRequest = Manual();
        updateRequest.Service = created.Service;
        updateRequest.AvailableFrom = created.AvailableFrom;
        updateRequest.AvailableBy = created.AvailableBy;
        updateRequest.Version = created.Version;
        using var update = await Send(HttpMethod.Put, $"{Path}/{created.Id}", token, JsonContent.Create(updateRequest));
        var updated = (await update.Content.ReadFromJsonAsync<ServiceCatalogueEntryDto>())!;
        Assert.That(updated.PriceMethod, Is.EqualTo(PriceMethod.Manual));

        using var stale = await Send(HttpMethod.Put, $"{Path}/{created.Id}", token, JsonContent.Create(updateRequest));
        await Problem(stale, HttpStatusCode.Conflict, "service_catalogue_update_conflict", "version");
        using var deletion = await Send(HttpMethod.Delete, $"{Path}/{created.Id}", token,
            JsonContent.Create(new DeleteServiceCatalogueEntryRequest(updated.Version)));
        Assert.That(deletion.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        using var missing = await Send(HttpMethod.Get, $"{Path}/{created.Id}", token);
        await Problem(missing, HttpStatusCode.NotFound, "service_catalogue_entry_not_found");

        using var auditResponse = await Send(HttpMethod.Get,
            $"{Path}/audit?entryId={created.Id}&page=1&pageSize=100&sortBy=timestamp&sortOrder=asc", token);
        var audit = (await auditResponse.Content.ReadFromJsonAsync<ServiceCatalogueAuditPageDto>())!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(audit.Items.Select(item => item.Action), Is.EqualTo(new[]
            {
                ServiceCatalogueAuditAction.Created,
                ServiceCatalogueAuditAction.Updated,
                ServiceCatalogueAuditAction.Deleted
            }));
            Assert.That(audit.Items[0].Before, Is.Null);
            Assert.That(audit.Items[0].After!.PriceMethod, Is.EqualTo(PriceMethod.Percent));
            Assert.That(audit.Items[1].Before!.PriceMethod, Is.EqualTo(PriceMethod.Percent));
            Assert.That(audit.Items[1].After!.PriceMethod, Is.EqualTo(PriceMethod.Manual));
            Assert.That(audit.Items[2].Before, Is.Not.Null);
            Assert.That(audit.Items[2].After, Is.Null);
            Assert.That(audit.Items.All(item => item.ActorId > 0 && item.ActorName.Length > 0), Is.True);
        }
    }

    [Test]
    public async Task EurInvalidShapesInclusiveOverlapAndMalformedFiltersReturnRussianProblems()
    {
        var token = await StaffToken(BackofficeRoles.Administrator);
        using var eur = await Send(HttpMethod.Post, Path, token, JsonContent.Create(Manual(Currency.Eur)));
        await Problem(eur, HttpStatusCode.BadRequest, "invalid_service_catalogue_currency", "currency");
        using var undefined = await Send(HttpMethod.Post, Path, token, JsonContent.Create(Manual((Currency)999)));
        await Problem(undefined, HttpStatusCode.BadRequest, "invalid_service_catalogue_currency", "currency");

        var firstRequest = Manual();
        firstRequest.AvailableBy = new DateOnly(2026, 1, 31);
        using var first = await Send(HttpMethod.Post, Path, token, JsonContent.Create(firstRequest));
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var touching = Manual(); touching.AvailableFrom = new DateOnly(2026, 1, 31);
        using var overlap = await Send(HttpMethod.Post, Path, token, JsonContent.Create(touching));
        await Problem(overlap, HttpStatusCode.Conflict, "service_catalogue_period_overlap", "availableFrom");

        foreach (var suffix in new[]
                 {
                     "/audit?page=0", "/audit?pageSize=101", "/audit?service=999",
                     "/audit?action=999", "/audit?entryId=0", "/audit?sortBy=unknown", "/audit?sortOrder=sideways"
                 })
        {
            using var response = await Send(HttpMethod.Get, Path + suffix, token);
            await Problem(response, HttpStatusCode.BadRequest, "invalid_service_catalogue_audit_filter");
        }
    }

    [Test]
    public async Task OpenStartIsAcceptedAndReturnedInCatalogueAndAudit()
    {
        var token = await StaffToken(BackofficeRoles.Administrator);
        var request = Manual();
        request.AvailableFrom = null;
        request.AvailableBy = new DateOnly(2026, 1, 31);
        using var creation = await Send(HttpMethod.Post, Path, token, JsonContent.Create(request));
        Assert.That(creation.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        using (var payload = JsonDocument.Parse(await creation.Content.ReadAsStringAsync()))
        {
            Assert.That(payload.RootElement.GetProperty("availableFrom").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }
        var created = (await creation.Content.ReadFromJsonAsync<ServiceCatalogueEntryDto>())!;
        Assert.That(created.AvailableFrom, Is.Null);
        using var listResponse = await Send(HttpMethod.Get, Path, token);
        var list = (await listResponse.Content.ReadFromJsonAsync<ServiceCatalogueListDto>())!;
        Assert.That(list.Items.Single().AvailableFrom, Is.Null);
        using var auditResponse = await Send(HttpMethod.Get, Path + "/audit?page=1&pageSize=10&sortBy=timestamp&sortOrder=asc", token);
        var audit = (await auditResponse.Content.ReadFromJsonAsync<ServiceCatalogueAuditPageDto>())!;
        Assert.That(audit.Items.Single().After!.AvailableFrom, Is.Null);
    }

    [Test]
    public async Task ProductDefinitionIsReadableButAllMutationsAreRejected()
    {
        var token = await StaffToken(BackofficeRoles.Administrator);
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var seed = new ServiceCatalogueEntry(ServiceKind.Product, PriceMethod.Manual,
                null, null, null, null, Currency.Usd, null, null, DateTimeOffset.UtcNow);
            database.ServiceCatalogueEntries.Add(seed);
            database.Entry(seed).Property(item => item.Id).CurrentValue = 1;
            await database.SaveChangesAsync();
        }
        using var read = await Send(HttpMethod.Get, Path + "/1", token);
        Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var product = (await read.Content.ReadFromJsonAsync<ServiceCatalogueEntryDto>())!;
        var request = Manual(); request.Service = ServiceKind.Product; request.AvailableFrom = null;
        using var create = await Send(HttpMethod.Post, Path, token, JsonContent.Create(request));
        await Problem(create, HttpStatusCode.Conflict, "service_catalogue_product_reserved", "service");
        request.Version = product.Version;
        using var update = await Send(HttpMethod.Put, Path + "/1", token, JsonContent.Create(request));
        await Problem(update, HttpStatusCode.Conflict, "service_catalogue_product_reserved", "service");
        using var delete = await Send(HttpMethod.Delete, Path + "/1", token,
            JsonContent.Create(new DeleteServiceCatalogueEntryRequest(product.Version)));
        await Problem(delete, HttpStatusCode.Conflict, "service_catalogue_product_reserved", "service");
    }

    [Test]
    public async Task AnonymousAndCustomerTokensCannotReachCatalogueEndpoints()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = new Customer
        {
            Phone = "+79990007777",
            Profile = new CustomerProfile(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        database.Customers.Add(customer);
        await database.SaveChangesAsync();
        var customerToken = scope.ServiceProvider.GetRequiredService<JwtTokenService>().CreateAccessToken(customer).Token;
        foreach (var token in new string?[] { null, customerToken })
        {
            using var response = await Send(HttpMethod.Get, Path + "/ops", token);
            await Problem(response, HttpStatusCode.Unauthorized, "invalid_backoffice_access_token");
        }
    }

    private static ServiceCatalogueWriteRequest Percent() => new()
    {
        Service = ServiceKind.WarehousePhoto,
        PriceMethod = PriceMethod.Percent,
        Percentage = 10.125m,
        Currency = Currency.Rub,
        MinimumAmount = 0,
        MaximumAmount = 100,
        AvailableFrom = new DateOnly(2026, 1, 1)
    };

    private static ServiceCatalogueWriteRequest Manual(Currency currency = Currency.Usd) => new()
    {
        Service = ServiceKind.InternationalDelivery,
        PriceMethod = PriceMethod.Manual,
        Currency = currency,
        AvailableFrom = new DateOnly(2026, 1, 1)
    };

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private static async Task<string> StaffToken(string role)
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var value = Guid.NewGuid().ToString("N");
        var user = new BackofficeUser
        {
            Email = $"{value}@catalogue.test",
            NormalizedEmail = $"{value}@catalogue.test",
            FirstName = "Тарифы",
            LastName = "Тест",
            PasswordHash = "unused",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            UserRoles = [new BackofficeUserRole { RoleCode = role }]
        };
        database.BackofficeUsers.Add(user);
        await database.SaveChangesAsync();
        return scope.ServiceProvider.GetRequiredService<BackofficeJwtTokenService>().CreateAccessToken(user).Token;
    }

    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status, string code, string? field = null)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(status), body);
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/problem+json"));
        Assert.That(response.Content.Headers.ContentLanguage, Does.Contain("ru"));
        using var json = JsonDocument.Parse(body);
        Assert.That(json.RootElement.GetProperty("code").GetString(), Is.EqualTo(code));
        Assert.That(json.RootElement.GetProperty("title").GetString(), Does.Match("[А-Яа-я]"));
        Assert.That(json.RootElement.GetProperty("detail").GetString(), Does.Match("[А-Яа-я]"));
        if (field is not null)
            Assert.That(json.RootElement.GetProperty("errors").TryGetProperty(field, out _), Is.True, body);
    }
}
