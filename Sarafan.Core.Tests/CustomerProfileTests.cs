// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class CustomerFlowTests
{
    private static readonly byte[] ProfilePng = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3, 4];

    private async Task<HttpResponseMessage> SaveProfileForm(string token, byte[]? photo = null,
        string type = "image/png", bool remove = false, string? email = null, string name = "Мария", IReadOnlyDictionary<string, string>? fields = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(name), "firstName");
        form.Add(new StringContent(remove ? "true" : "false"), "removePhoto");
        if (email is not null) form.Add(new StringContent(email), "email");
        if (fields is not null)
            foreach (var (key, value) in fields) form.Add(new StringContent(value), key);
        if (photo is not null)
        {
            var content = new ByteArrayContent(photo);
            content.Headers.ContentType = new MediaTypeHeaderValue(type);
            form.Add(content, "photo", "avatar.png");
        }
        using var request = AuthorizedRequest(HttpMethod.Put, "/api/v1/customers/me/profile", token);
        request.Content = form;
        return await _client.SendAsync(request);
    }

    [Test]
    public async Task ProfileSave_CombinesPartialFieldsAndPhotoReplacementRemovalAndKeep()
    {
        var (session, _) = await Register(NextPhone());
        using var created = await SaveProfileForm(session.AccessToken, ProfilePng, email: "maria@example.com");
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var first = (await created.Content.ReadFromJsonAsync<CustomerDto>())!;
        Assert.Multiple(() =>
        {
            Assert.That(first.HasPhoto, Is.True);
            Assert.That(first.Profile.FirstName, Is.EqualTo("Мария"));
            Assert.That(first.Profile.Phone, Is.EqualTo(session.Customer.Phone));
            Assert.That(first.State, Is.EqualTo(CustomerState.Preliminary));
        });
        var replacedBytes = ProfilePng.Concat(new byte[] { 7 }).ToArray();
        using var replaced = await SaveProfileForm(session.AccessToken, replacedBytes, name: "Анна");
        Assert.That(replaced.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var retained = await SaveProfileForm(session.AccessToken, name: "");
        var empty = (await retained.Content.ReadFromJsonAsync<CustomerDto>())!;
        Assert.Multiple(() =>
        {
            Assert.That(empty.HasPhoto, Is.True);
            Assert.That(empty.Profile.FirstName, Is.Null);
            Assert.That(empty.Profile.Email, Is.Null);
        });
        using var getPhoto = AuthorizedRequest(HttpMethod.Get, "/api/v1/customers/me/photo", session.AccessToken);
        using var photoResponse = await _client.SendAsync(getPhoto);
        Assert.That(await photoResponse.Content.ReadAsByteArrayAsync(), Is.EqualTo(replacedBytes));
        using var removed = await SaveProfileForm(session.AccessToken, remove: true, name: "Иван");
        var last = (await removed.Content.ReadFromJsonAsync<CustomerDto>())!;
        Assert.That(last.HasPhoto, Is.False);
        Assert.That(last.Profile.FirstName, Is.EqualTo("Иван"));
        using var repeated = await SaveProfileForm(session.AccessToken, remove: true);
        Assert.That(repeated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [TestCase("type", "invalid_photo_type")]
    [TestCase("content", "invalid_photo_content")]
    [TestCase("empty", "invalid_photo_size")]
    [TestCase("size", "invalid_photo_size")]
    [TestCase("both", "validation_failed")]
    [TestCase("email", "validation_failed")]
    public async Task ProfileSave_InvalidDraftLeavesSavedFieldsAndPhotoUntouched(string scenario, string expected)
    {
        var (session, _) = await Register(NextPhone());
        using var initial = await SaveProfileForm(session.AccessToken, ProfilePng, email: "saved@example.com", name: "Сохранено");
        initial.EnsureSuccessStatusCode();
        var bytes = scenario switch
        {
            "content" => new byte[] { 1, 2, 3 },
            "empty" => [],
            "size" => new byte[5 * 1024 * 1024 + 1],
            _ => ProfilePng
        };
        using var failed = await SaveProfileForm(session.AccessToken, bytes,
            type: scenario == "type" ? "text/plain" : "image/png",
            remove: scenario == "both", email: scenario == "email" ? "invalid" : "changed@example.com", name: "Несохранено");
        Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await AssertPhotoProblem(failed, expected);
        if (scenario == "both")
            Assert.That(problem.Errors!["photo"], Is.EqualTo(new[] { "Выберите загрузку или удаление фотографии." }));
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = await database.Customers.SingleAsync(row => row.Id == session.Customer.Id);
        Assert.Multiple(() =>
        {
            Assert.That(customer.FirstName, Is.EqualTo("Сохранено"));
            Assert.That(customer.Email, Is.EqualTo("saved@example.com"));
        });
        Assert.That((await database.CustomerPhotos.SingleAsync(row => row.CustomerId == customer.Id)).Content, Is.EqualTo(ProfilePng));
    }

    [TestCase("type", "invalid_photo_type")]
    [TestCase("content", "invalid_photo_content")]
    [TestCase("empty", "invalid_photo_size")]
    [TestCase("size", "invalid_photo_size")]
    public async Task LegacyPhoto_InvalidUploadPreservesSavedPhoto(string scenario, string expected)
    {
        var (session, _) = await Register(NextPhone());
        using var initial = await SaveProfileForm(session.AccessToken, ProfilePng, name: "Сохранено");
        initial.EnsureSuccessStatusCode();
        var saved = (await initial.Content.ReadFromJsonAsync<CustomerDto>())!;
        var bytes = scenario switch
        {
            "content" => new byte[] { 1, 2, 3 },
            "empty" => [],
            "size" => new byte[5 * 1024 * 1024 + 1],
            _ => ProfilePng
        };
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(scenario == "type" ? "text/plain" : "image/png");
        form.Add(content, "file", "avatar.png");
        using var request = AuthorizedRequest(HttpMethod.Put, "/api/v1/customers/me/photo", session.AccessToken);
        request.Content = form;
        using var failed = await _client.SendAsync(request);
        await AssertPhotoProblem(failed, expected);
        using var current = AuthorizedRequest(HttpMethod.Get, "/api/v1/customers/me", session.AccessToken);
        using var unchanged = await _client.SendAsync(current);
        Assert.That(await unchanged.Content.ReadFromJsonAsync<CustomerDto>(), Is.EqualTo(saved));
        using var photo = AuthorizedRequest(HttpMethod.Get, "/api/v1/customers/me/photo", session.AccessToken);
        using var response = await _client.SendAsync(photo);
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(ProfilePng));
    }

    private static async Task<SarafanProblemDetails> AssertPhotoProblem(HttpResponseMessage response, string expected)
    {
        var problem = (await response.Content.ReadFromJsonAsync<SarafanProblemDetails>())!;
        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(SarafanProblemDetailsFactory.MediaType));
            Assert.That(response.Content.Headers.ContentLanguage, Does.Contain("ru"));
            Assert.That(problem.Status, Is.EqualTo((int)response.StatusCode));
            Assert.That(problem.Code, Is.EqualTo(expected));
            Assert.That(problem.Type, Is.EqualTo(SarafanProblemDetailsFactory.TypeBase + expected.Replace('_', '-')));
            Assert.That(problem.Instance, Is.EqualTo("urn:sarafan:problem:" + problem.TraceId));
            Assert.That(problem.Title, Does.Match("[А-Яа-я]"));
            Assert.That(problem.Detail, Does.Match("[А-Яа-я]"));
        });
        return problem;
    }

    [Test]
    public async Task ProfileSave_BindsAndClearsPassportAndAddressFields()
    {
        var (session, _) = await Register(NextPhone());
        var fields = new Dictionary<string, string>
        {
            ["lastName"] = "Петрова",
            ["patronymic"] = "Ивановна",
            ["passportSeries"] = "1234",
            ["passportNumber"] = "654321",
            ["passportIssueDate"] = "2010-02-03",
            ["passportIssuedBy"] = "ОВД",
            ["inn"] = "123456789012",
            ["postalCode"] = "101000",
            ["city"] = "Москва",
            ["address"] = "Улица, 1"
        };
        using var saved = await SaveProfileForm(session.AccessToken, ProfilePng, fields: fields);
        saved.EnsureSuccessStatusCode();
        var profile = (await saved.Content.ReadFromJsonAsync<CustomerDto>())!.Profile;
        Assert.Multiple(() =>
        {
            Assert.That(profile.LastName, Is.EqualTo(fields["lastName"]));
            Assert.That(profile.Patronymic, Is.EqualTo(fields["patronymic"]));
            Assert.That(profile.PassportSeries, Is.EqualTo(fields["passportSeries"]));
            Assert.That(profile.PassportNumber, Is.EqualTo(fields["passportNumber"]));
            Assert.That(profile.PassportIssueDate, Is.EqualTo(new DateOnly(2010, 2, 3)));
            Assert.That(profile.PassportIssuedBy, Is.EqualTo(fields["passportIssuedBy"]));
            Assert.That(profile.Inn, Is.EqualTo(fields["inn"]));
            Assert.That(profile.PostalCode, Is.EqualTo(fields["postalCode"]));
            Assert.That(profile.City, Is.EqualTo(fields["city"]));
            Assert.That(profile.Address, Is.EqualTo(fields["address"]));
        });
        fields["passportIssueDate"] = "invalid-date";
        using var invalid = await SaveProfileForm(session.AccessToken, remove: true, fields: fields);
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(problem.GetProperty("errors").TryGetProperty("passportIssueDate", out _), Is.True);
        using var current = AuthorizedRequest(HttpMethod.Get, "/api/v1/customers/me", session.AccessToken);
        using var unchanged = await _client.SendAsync(current);
        var persisted = (await unchanged.Content.ReadFromJsonAsync<CustomerDto>())!;
        Assert.That(persisted.Profile, Is.EqualTo(profile));
        Assert.That(persisted.HasPhoto, Is.True);
        using var cleared = await SaveProfileForm(session.AccessToken, remove: true,
            fields: fields.ToDictionary(item => item.Key, _ => ""));
        cleared.EnsureSuccessStatusCode();
        var empty = (await cleared.Content.ReadFromJsonAsync<CustomerDto>())!;
        Assert.That(empty.HasPhoto, Is.False);
        Assert.That(empty.Profile, Is.EqualTo(new CustomerProfileDto(
            session.Customer.Phone, null, "Мария", null, null, null, null, null, null, null, null, null, null)));
    }

    [Test]
    public async Task ProfileSave_DoesNotRewriteAnExistingOrderRecipientSnapshot()
    {
        var (session, _) = await Register(NextPhone());
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = await database.Customers.SingleAsync(row => row.Id == session.Customer.Id);
        customer.FirstName = "Снимок";
        var snapshot = new OrderCheckoutDto(CustomerProfileDto.From(customer), new("courier", "Курьер", "101000, Москва, Старый адрес"));
        var order = new Order(customer.Id, 1, "https://example.com/product", 1, null, Guid.NewGuid(), DateTimeOffset.UtcNow);
        order.UpdatePricing(DateTimeOffset.UtcNow, confirmed: true);
        order.SaveCheckout(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)), DateTimeOffset.UtcNow);
        database.Orders.Add(order);
        await database.SaveChangesAsync();
        using var saved = await SaveProfileForm(session.AccessToken, ProfilePng, name: "Новый профиль");
        saved.EnsureSuccessStatusCode();
        database.ChangeTracker.Clear();
        Assert.That((await database.Orders.SingleAsync(row => row.Id == order.Id)).CheckoutData,
            Is.EqualTo(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
    }
}
