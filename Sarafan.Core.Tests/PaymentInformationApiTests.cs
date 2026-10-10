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
public sealed class PaymentInformationApiTests
{
    private const string Root = "/api/v1/backoffice/payment-information-bundles";
    private const string Current = "/api/v1/payment-information/current";
    private HttpClient _client = null!;
    [SetUp] public async Task Setup() { await IntegrationTestEnvironment.ResetAsync(); _client = IntegrationTestEnvironment.Factory.CreateClient(); }
    [TearDown] public void TearDown() => _client.Dispose();

    [Test]
    public async Task ManagementRequiresAdministratorAndCustomerIdentityCannotCrossBoundaries()
    {
        var staff = await StaffToken(BackofficeRoles.Administrator);
        using var creation = await Send(HttpMethod.Post, Root, staff, Form());
        Assert.That(creation.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var row = (await creation.Content.ReadFromJsonAsync<PaymentBundleDto>())!;
        var customer = await CustomerToken();
        foreach (var token in new[] { null, customer, await StaffToken(BackofficeRoles.Operator), await StaffToken(BackofficeRoles.ShiftManager), await StaffToken(BackofficeRoles.SeniorOperator) })
        {
            var expected = token is null || token == customer ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            foreach (var path in new[] { Root, Root + "/ops", Root + "/" + row.Id, row.QrUrl! })
            {
                using var read = await Send(HttpMethod.Get, path, token);
                await Problem(read, expected, expected == HttpStatusCode.Unauthorized ? "invalid_backoffice_access_token" : "access_denied");
            }
            foreach (var action in new[] { "copy", "enable", "disable" })
            {
                using var response = await Send(HttpMethod.Post, $"{Root}/{row.Id}/{action}", token, JsonContent.Create(new { version = row.Version, expectedEnabled = (object?)null }));
                Assert.That(response.StatusCode, Is.EqualTo(expected));
            }
            using var create = await Send(HttpMethod.Post, Root, token, Form());
            using var update = await Send(HttpMethod.Put, $"{Root}/{row.Id}", token, Form(row.Version));
            using var delete = await Send(HttpMethod.Delete, $"{Root}/{row.Id}", token, JsonContent.Create(new PaymentBundleVersionRequest(row.Version)));
            Assert.That(new[] { create, update, delete }.All(item => item.StatusCode == expected), Is.True);
        }
        foreach (var path in new[] { Current, Current + "/qr?v=wrong" })
        {
            using var anonymous = await Send(HttpMethod.Get, path, null);
            Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            using var staffRead = await Send(HttpMethod.Get, path, staff);
            Assert.That(staffRead.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
    }

    [Test]
    public async Task HttpLifecycleServesOnlyTheEnabledDigestAndCanDeletePublishedInactiveRows()
    {
        var admin = await StaffToken(BackofficeRoles.Administrator);
        var customer = await CustomerToken();
        using var empty = await Send(HttpMethod.Get, Current, customer);
        Assert.That((await empty.Content.ReadFromJsonAsync<CurrentPaymentInformationDto>())!.PaymentInformation, Is.Null);
        Assert.That(empty.Headers.CacheControl!.NoStore, Is.True);
        using var ops = await Send(HttpMethod.Get, Root + "/ops", admin);
        Assert.That((await ops.Content.ReadFromJsonAsync<PaymentBundleOpsDto>())!.CanManage, Is.True);
        using var create = await Send(HttpMethod.Post, Root, admin, Form());
        var draft = (await create.Content.ReadFromJsonAsync<PaymentBundleDto>())!;
        Assert.That(create.Headers.Location!.ToString(), Does.EndWith("/" + draft.Id));
        using var update = await Send(HttpMethod.Put, $"{Root}/{draft.Id}", admin, Form(draft.Version, qr: false));
        var saved = (await update.Content.ReadFromJsonAsync<PaymentBundleDto>())!;
        using var draftQr = await Send(HttpMethod.Get, saved.QrUrl!, admin);
        Assert.That(draftQr.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(draftQr.Headers.CacheControl!.NoStore, Is.True);
        Assert.That(draftQr.Headers.GetValues("X-Content-Type-Options").Single(), Is.EqualTo("nosniff"));
        using var missingObservation = await Send(HttpMethod.Post, $"{Root}/{saved.Id}/enable", admin, JsonContent.Create(new { version = saved.Version }));
        await Problem(missingObservation, HttpStatusCode.BadRequest, "validation_failed");
        using var enabled = await Send(HttpMethod.Post, $"{Root}/{saved.Id}/enable", admin, JsonContent.Create(new EnablePaymentBundleRequest(saved.Version, null)));
        Assert.That(enabled.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var active = (await enabled.Content.ReadFromJsonAsync<PaymentBundleDto>())!;
        using var get = await Send(HttpMethod.Get, $"{Root}/{active.Id}", admin);
        Assert.That((await get.Content.ReadFromJsonAsync<PaymentBundleDto>())!.Enabled, Is.True);
        using var list = await Send(HttpMethod.Get, Root + "?state=enabled&sortBy=createdAt&sortOrder=asc", admin);
        var page = (await list.Content.ReadFromJsonAsync<PaymentBundlePageDto>())!;
        Assert.That(page.Items.Single().Id, Is.EqualTo(active.Id));
        using var current = await Send(HttpMethod.Get, Current, customer);
        var visible = (await current.Content.ReadFromJsonAsync<CurrentPaymentInformationDto>())!.PaymentInformation!;
        Assert.That(visible.BundleId, Is.EqualTo(active.Id));
        using var qr = await Send(HttpMethod.Get, visible.QrUrl, customer);
        Assert.That(await qr.Content.ReadAsByteArrayAsync(), Is.EqualTo(StoreServiceTests.Png));
        Assert.That(qr.Headers.CacheControl!.NoStore, Is.True);
        using var wrongDigest = await Send(HttpMethod.Get, Current + "/qr?v=" + new string('a', 64), customer);
        await Problem(wrongDigest, HttpStatusCode.NotFound, "resource_not_found");
        using var frozen = await Send(HttpMethod.Put, $"{Root}/{active.Id}", admin, Form(active.Version));
        await Problem(frozen, HttpStatusCode.Conflict, "payment_bundle_frozen");
        using var deleteActive = await Send(HttpMethod.Delete, $"{Root}/{active.Id}", admin, JsonContent.Create(new PaymentBundleVersionRequest(active.Version)));
        await Problem(deleteActive, HttpStatusCode.Conflict, "payment_bundle_enabled");
        using var copied = await Send(HttpMethod.Post, $"{Root}/{active.Id}/copy", admin, JsonContent.Create(new PaymentBundleVersionRequest(active.Version)));
        Assert.That(copied.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That((await copied.Content.ReadFromJsonAsync<PaymentBundleDto>())!.CanEdit, Is.True);
        using var disabled = await Send(HttpMethod.Post, $"{Root}/{active.Id}/disable", admin, JsonContent.Create(new PaymentBundleVersionRequest(active.Version)));
        var inactive = (await disabled.Content.ReadFromJsonAsync<PaymentBundleDto>())!;
        Assert.That(inactive.CanDelete, Is.True);
        using var hiddenQr = await Send(HttpMethod.Get, visible.QrUrl, customer);
        await Problem(hiddenQr, HttpStatusCode.NotFound, "resource_not_found");
        using var none = await Send(HttpMethod.Get, Current, customer);
        Assert.That((await none.Content.ReadFromJsonAsync<CurrentPaymentInformationDto>())!.PaymentInformation, Is.Null);
        using var deleted = await Send(HttpMethod.Delete, $"{Root}/{inactive.Id}", admin, JsonContent.Create(new PaymentBundleVersionRequest(inactive.Version)));
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    [Test]
    public async Task InvalidMultipartAndSelectionRequestsReturnStructuredRussianProblems()
    {
        var admin = await StaffToken(BackofficeRoles.Administrator);
        using var create = await Send(HttpMethod.Post, Root, admin, new MultipartFormDataContent { { new StringContent(""), "RecipientName" } });
        Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.Created), await create.Content.ReadAsStringAsync());
        var row = (await create.Content.ReadFromJsonAsync<PaymentBundleDto>())!;
        Assert.That(row.CanEnable, Is.False);
        using var missing = await Send(HttpMethod.Put, $"{Root}/{row.Id}", admin, Form());
        await Problem(missing, HttpStatusCode.BadRequest, "invalid_payment_bundle_version");
        using var stale = await Send(HttpMethod.Put, $"{Root}/{row.Id}", admin, Form(Guid.NewGuid()));
        await Problem(stale, HttpStatusCode.Conflict, "payment_bundle_update_conflict");
        using var incomplete = await Send(HttpMethod.Post, $"{Root}/{row.Id}/enable", admin, JsonContent.Create(new EnablePaymentBundleRequest(row.Version, null)));
        await Problem(incomplete, HttpStatusCode.BadRequest, "validation_failed");
        using var filter = await Send(HttpMethod.Get, Root + "?state=unknown", admin);
        await Problem(filter, HttpStatusCode.BadRequest, "invalid_payment_bundle_filter");
        using var copy = await Send(HttpMethod.Post, $"{Root}/{row.Id}/copy", admin, JsonContent.Create(new PaymentBundleVersionRequest(row.Version)));
        await Problem(copy, HttpStatusCode.Conflict, "payment_bundle_copy_unavailable");
        using var wrongMedia = await Send(HttpMethod.Post, Root, admin, JsonContent.Create(new { }));
        await Problem(wrongMedia, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        using var invalidImage = Form(qr: false);
        var bytes = new ByteArrayContent([1, 2, 3]); bytes.Headers.ContentType = new("image/png"); invalidImage.Add(bytes, "qr", "qr.png");
        using var invalid = await Send(HttpMethod.Post, Root, admin, invalidImage);
        await Problem(invalid, HttpStatusCode.BadRequest, "validation_failed");
        using var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        Assert.That(problem.RootElement.GetProperty("errors").TryGetProperty("qr", out _), Is.True);
    }

    private static MultipartFormDataContent Form(Guid? version = null, bool qr = true)
    {
        var input = PaymentInformationTests.Complete(qr);
        var form = new MultipartFormDataContent();
        foreach (var field in typeof(PaymentInformationWriteRequest).GetProperties().Where(item => item.PropertyType == typeof(string) || item.Name == "RecipientType"))
            if (field.GetValue(input) is { } value) form.Add(new StringContent(value is PaymentRecipientType type ? ((int)type).ToString() : value.ToString()!), field.Name);
        if (version.HasValue) form.Add(new StringContent(version.ToString()!), "Version");
        if (qr)
        {
            var content = new ByteArrayContent(StoreServiceTests.Png); content.Headers.ContentType = new("image/png"); form.Add(content, "Qr", "qr.png");
        }
        return form;
    }
    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        return await _client.SendAsync(request);
    }
    private static async Task<string> StaffToken(string role)
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new BackofficeUser
        {
            Email = Guid.NewGuid() + "@test",
            NormalizedEmail = Guid.NewGuid() + "@test",
            FirstName = "Payment",
            LastName = "Test",
            PasswordHash = "unused",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            UserRoles = [new() { RoleCode = role }]
        };
        db.Add(user); await db.SaveChangesAsync();
        return scope.ServiceProvider.GetRequiredService<BackofficeJwtTokenService>().CreateAccessToken(user).Token;
    }
    private static async Task<string> CustomerToken()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = new Customer { Phone = "+79999999999", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        db.Add(customer); await db.SaveChangesAsync();
        return scope.ServiceProvider.GetRequiredService<JwtTokenService>().CreateAccessToken(customer).Token;
    }
    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(status), text);
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/problem+json"));
        var problem = JsonSerializer.Deserialize<SarafanProblemDetails>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.That(problem.Type, Is.EqualTo("https://sarafan.sw.consulting/problems/" + code.Replace('_', '-')));
        Assert.That(problem.Code, Is.EqualTo(code)); Assert.That(problem.Title, Does.Match("[А-Яа-я]"));
        Assert.That(problem.Detail, Does.Match("[А-Яа-я]"));
    }
}
