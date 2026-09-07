// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Sarafan.Core.RestModels;

namespace Sarafan.Core.Tests;

[TestFixture]
[NonParallelizable]
public sealed class CustomerFlowTests
{
    private static int _phoneSequence = 100;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _client = IntegrationTestEnvironment.Factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false
            });
    }

    [TearDown]
    public void TearDown() => _client.Dispose();

    [Test]
    public async Task Register_ProfilePhotoAndPersistenceFlow_Works()
    {
        var phone = NextPhone();
        var (session, cookie) = await Register(phone);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(session.AccessToken, Is.Not.Empty);
            Assert.That(session.Customer.Phone, Is.EqualTo(phone));
            Assert.That(session.Customer.State, Is.EqualTo("preliminary"));
            Assert.That(cookie, Does.StartWith("sarafan.refresh="));
        }

        var update = new CustomerProfileUpdateRequest
        {
            LastName = " Ковалёва ",
            FirstName = " Мария ",
            Patronymic = "Ивановна",
            Email = "MARIA@example.com",
            PassportSeries = "4510",
            PassportNumber = "123456",
            PassportIssueDate = new DateOnly(2020, 2, 3),
            PassportIssuedBy = "ОВД",
            Inn = "123456789012",
            PostalCode = "101000",
            City = "Москва",
            Address = "ул. Примерная, 1"
        };
        using var updateRequest = AuthorizedRequest(HttpMethod.Put, "/api/v1/customers/me", session.AccessToken);
        updateRequest.Content = JsonContent.Create(update);
        using var updateResponse = await _client.SendAsync(updateRequest);
        var updated = await updateResponse.Content.ReadFromJsonAsync<CustomerDto>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(updateResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(updated, Is.Not.Null);
            Assert.That(updated!.State, Is.EqualTo("complete"));
            Assert.That(updated.Profile.FirstName, Is.EqualTo("Мария"));
            Assert.That(updated.Profile.Email, Is.EqualTo("maria@example.com"));
            Assert.That(updated.Profile.Phone, Is.EqualTo(phone));
        }

        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3, 4 };
        using var photoForm = new MultipartFormDataContent();
        using var photoContent = new ByteArrayContent(png);
        photoContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        photoForm.Add(photoContent, "file", "avatar.png");
        using var photoPut = AuthorizedRequest(HttpMethod.Put, "/api/v1/customers/me/photo", session.AccessToken);
        photoPut.Content = photoForm;
        using var photoPutResponse = await _client.SendAsync(photoPut);
        Assert.That(photoPutResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        using var restartedClient = IntegrationTestEnvironment.Factory.CreateClient();
        using var getRequest = AuthorizedRequest(HttpMethod.Get, "/api/v1/customers/me", session.AccessToken);
        using var getResponse = await restartedClient.SendAsync(getRequest);
        var persisted = await getResponse.Content.ReadFromJsonAsync<CustomerDto>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(getResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(persisted?.Profile.Address, Is.EqualTo("ул. Примерная, 1"));
            Assert.That(persisted?.HasPhoto, Is.True);
        }

        using var photoGet = AuthorizedRequest(HttpMethod.Get, "/api/v1/customers/me/photo", session.AccessToken);
        using var photoGetResponse = await restartedClient.SendAsync(photoGet);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(photoGetResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(photoGetResponse.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/png"));
            Assert.That(await photoGetResponse.Content.ReadAsByteArrayAsync(), Is.EqualTo(png));
        }

        using var deleteRequest = AuthorizedRequest(HttpMethod.Delete, "/api/v1/customers/me/photo", session.AccessToken);
        using var deleteResponse = await restartedClient.SendAsync(deleteRequest);
        Assert.That(deleteResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    [Test]
    public async Task Verification_RejectsWrongCodeAndMissingConsents()
    {
        var phone = NextPhone();
        var onboarding = await ConsentTestData.Onboarding(_client, phone);
        using var wrongCode = await _client.PostAsJsonAsync("/api/v1/auth/code/verify", new
        {
            phone,
            purpose = "register",
            code = WrongVerificationCode(phone),
            termsAccepted = true,
            onboardingToken = onboarding
        });
        using var missingConsents = await _client.PostAsJsonAsync("/api/v1/auth/code/verify", new
        {
            phone,
            purpose = "register",
            code = VerificationCode(phone),
            termsAccepted = false,
            personalDataAccepted = true
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrongCode.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(missingConsents.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }
    }

    [Test]
    public async Task CodeRequest_IsRateLimitedPerPhone()
    {
        var phone = NextPhone();
        var responses = new List<HttpResponseMessage>();
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                responses.Add(await _client.PostAsJsonAsync("/api/v1/auth/code/request", await ConsentTestData.Request(_client, phone)));
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(responses.Take(3).Select(item => item.StatusCode),
                    Is.All.EqualTo(HttpStatusCode.Accepted));
                Assert.That(responses[3].StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Test]
    public async Task CustomerProfile_RequiresAuthentication()
    {
        using var response = await _client.GetAsync("/api/v1/customers/me");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Login_WithEquivalentPhoneFormat_ReusesCustomer()
    {
        var phone = NextPhone();
        var (registered, _) = await Register(phone);
        var nationalFormat = $"8{phone[2..]}";

        using var loginResponse = await _client.PostAsJsonAsync("/api/v1/auth/code/verify", new
        {
            phone = nationalFormat,
            purpose = "login",
            code = VerificationCode(phone)
        });
        var loggedIn = await loginResponse.Content.ReadFromJsonAsync<AuthenticationSessionDto>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loginResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(loggedIn?.Customer.Id, Is.EqualTo(registered.Customer.Id));
            Assert.That(loggedIn?.Customer.Phone, Is.EqualTo(phone));
        }
    }

    [Test]
    public async Task Refresh_RotatesAndReuseRevokesFamily()
    {
        var (_, firstCookie) = await Register(NextPhone());
        using var firstRefresh = RequestWithCookie(HttpMethod.Post, "/api/v1/auth/refresh", firstCookie);
        using var firstRefreshResponse = await _client.SendAsync(firstRefresh);
        var secondCookie = RefreshCookie(firstRefreshResponse);

        using var reuse = RequestWithCookie(HttpMethod.Post, "/api/v1/auth/refresh", firstCookie);
        using var reuseResponse = await _client.SendAsync(reuse);
        using var revokedFamily = RequestWithCookie(HttpMethod.Post, "/api/v1/auth/refresh", secondCookie);
        using var revokedFamilyResponse = await _client.SendAsync(revokedFamily);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstRefreshResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(secondCookie, Is.Not.EqualTo(firstCookie));
            Assert.That(reuseResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(revokedFamilyResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
    }

    [Test]
    public async Task Logout_RevokesRefreshFamily()
    {
        var (_, cookie) = await Register(NextPhone());
        using var logout = RequestWithCookie(HttpMethod.Post, "/api/v1/auth/logout", cookie);
        using var logoutResponse = await _client.SendAsync(logout);
        using var refresh = RequestWithCookie(HttpMethod.Post, "/api/v1/auth/refresh", cookie);
        using var refreshResponse = await _client.SendAsync(refresh);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logoutResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(refreshResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
    }

    [Test]
    public async Task Photo_RejectsContentTypeMismatch()
    {
        var (session, _) = await Register(NextPhone());
        using var form = new MultipartFormDataContent();
        using var content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", "fake.png");
        using var request = AuthorizedRequest(HttpMethod.Put, "/api/v1/customers/me/photo", session.AccessToken);
        request.Content = form;
        using var response = await _client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    private async Task<(AuthenticationSessionDto Session, string Cookie)> Register(string phone)
    {
        var onboarding = await ConsentTestData.Onboarding(_client, phone);

        using var response = await _client.PostAsJsonAsync("/api/v1/auth/code/verify", new
        {
            phone,
            purpose = "register",
            code = VerificationCode(phone),
            termsAccepted = true,
            onboardingToken = onboarding
        });
        var session = await response.Content.ReadFromJsonAsync<AuthenticationSessionDto>();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(session, Is.Not.Null);
        return (session!, RefreshCookie(response));
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage RequestWithCookie(HttpMethod method, string path, string cookie)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        return request;
    }

    private static string RefreshCookie(HttpResponseMessage response)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("sarafan.refresh=", StringComparison.Ordinal));
        Assert.That(setCookie, Does.Contain("path=/api/v1/auth").IgnoreCase);
        return setCookie.Split(';', 2)[0];
    }

    private static string NextPhone()
        => $"+7999{Interlocked.Increment(ref _phoneSequence):D7}";

    private static string VerificationCode(string phone) => phone[^4..];

    private static string WrongVerificationCode(string phone)
        => VerificationCode(phone) == "0000" ? "0001" : "0000";
}
