// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Sarafan.Core.Authentication;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[NonParallelizable]
public sealed class BackofficeFlowTests
{
    private static int _emailSequence;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _client = IntegrationTestEnvironment.Factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
                BaseAddress = new Uri("https://localhost")
            });
    }

    [TearDown]
    public void TearDown() => _client.Dispose();

    [Test]
    public async Task BootstrapAdministrator_CanLoginReadIdentityAndRoleCatalog()
    {
        var session = await Login(
            _client,
            IntegrationTestEnvironment.BackofficeEmail.ToUpperInvariant(),
            IntegrationTestEnvironment.BackofficePassword);

        using var me = await SendAuthorized(
            _client,
            HttpMethod.Get,
            "/api/v1/backoffice/auth/me",
            session.AccessToken);
        var current = await me.Content.ReadFromJsonAsync<BackofficeIdentityDto>();
        using var roles = await SendAuthorized(
            _client,
            HttpMethod.Get,
            "/api/v1/backoffice/roles",
            session.AccessToken);
        var catalog = await roles.Content.ReadFromJsonAsync<List<BackofficeRoleDto>>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(session.User.Email, Is.EqualTo(IntegrationTestEnvironment.BackofficeEmail));
            Assert.That(session.User.FirstName, Is.EqualTo("Maxim"));
            Assert.That(session.User.LastName, Is.EqualTo("Samsonov"));
            Assert.That(session.User.Roles, Is.EqualTo(new[] { BackofficeRoles.Administrator }));
            Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(current?.Id, Is.EqualTo(session.User.Id));
            Assert.That(roles.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(catalog?.Select(item => item.Code), Is.EquivalentTo(BackofficeRoles.Codes));
        }
    }

    [Test]
    public async Task Login_UsesGenericFailureForWrongOrUnknownCredentials()
    {
        using var wrongPassword = await _client.PostAsJsonAsync(
            "/api/v1/backoffice/auth/login",
            new BackofficeLoginRequest
            {
                Email = IntegrationTestEnvironment.BackofficeEmail,
                Password = "wrong"
            });
        using var unknownUser = await _client.PostAsJsonAsync(
            "/api/v1/backoffice/auth/login",
            new BackofficeLoginRequest
            {
                Email = NextEmail(),
                Password = "Wrong_backoffice_password"
            });

        var wrongProblem = await ReadProblem(wrongPassword);
        var unknownProblem = await ReadProblem(unknownUser);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrongPassword.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(unknownUser.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(wrongProblem.Code, Is.EqualTo("backoffice_login_failed"));
            Assert.That(unknownProblem.Code, Is.EqualTo(wrongProblem.Code));
            Assert.That(unknownProblem.Title, Is.EqualTo(wrongProblem.Title));
            Assert.That(unknownProblem.Detail, Is.EqualTo(wrongProblem.Detail));
        }
    }

    [Test]
    public async Task CustomerAndBackofficeTokens_AreNotInterchangeable()
    {
        var backoffice = await Login(
            _client,
            IntegrationTestEnvironment.BackofficeEmail,
            IntegrationTestEnvironment.BackofficePassword);
        string customerToken;
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            customerToken = scope.ServiceProvider.GetRequiredService<JwtTokenService>()
                .CreateAccessToken(new Customer
                {
                    Id = backoffice.User.Id,
                    Phone = "+79990000000"
                })
                .Token;
        }

        var protectedBackofficePaths = new[]
        {
            "/api/v1/backoffice/auth/me",
            "/api/v1/backoffice/users",
            "/api/v1/backoffice/users/ops",
            "/api/v1/backoffice/roles"
        };
        foreach (var path in protectedBackofficePaths)
        {
            using var customerAtBackoffice = await SendAuthorized(
                _client,
                HttpMethod.Get,
                path,
                customerToken);
            Assert.That(customerAtBackoffice.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), path);
            Assert.That((await ReadProblem(customerAtBackoffice)).Code,
                Is.EqualTo("invalid_backoffice_access_token"),
                path);
        }

        using var backofficeAtCustomer = await SendAuthorized(
            _client,
            HttpMethod.Get,
            "/api/v1/customers/me",
            backoffice.AccessToken);

        var customerProblem = await ReadProblem(backofficeAtCustomer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(backofficeAtCustomer.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(customerProblem.Code, Is.EqualTo("invalid_access_token"));
        }
    }

    [Test]
    public async Task Refresh_RotatesDedicatedCookieAndReuseRevokesFamily()
    {
        using var login = await _client.PostAsJsonAsync(
            "/api/v1/backoffice/auth/login",
            new BackofficeLoginRequest
            {
                Email = IntegrationTestEnvironment.BackofficeEmail,
                Password = IntegrationTestEnvironment.BackofficePassword
            });
        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var originalCookie = ExtractRefreshCookie(login);

        using var refresh = await _client.PostAsync("/api/v1/backoffice/auth/refresh", null);
        Assert.That(refresh.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var replayClient = IntegrationTestEnvironment.Factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
                BaseAddress = new Uri("https://localhost")
            });
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/backoffice/auth/refresh");
        replayRequest.Headers.Add("Cookie", originalCookie);
        using var replay = await replayClient.SendAsync(replayRequest);
        using var revokedCurrent = await _client.PostAsync("/api/v1/backoffice/auth/refresh", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await ReadProblem(replay)).Code, Is.EqualTo("invalid_backoffice_refresh_token"));
            Assert.That(revokedCurrent.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await ReadProblem(revokedCurrent)).Code, Is.EqualTo("invalid_backoffice_refresh_token"));
        }
    }

    [Test]
    public async Task LogoutWithoutSessionIsIdempotentAndMissingRefreshIsRejected()
    {
        using var logout = await _client.PostAsync("/api/v1/backoffice/auth/logout", null);
        using var refresh = await _client.PostAsync("/api/v1/backoffice/auth/refresh", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(refresh.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await ReadProblem(refresh)).Code, Is.EqualTo("invalid_backoffice_refresh_token"));
        }
    }

    [Test]
    public async Task Logout_RevokesExistingSessionAndUnknownCookieIsHarmless()
    {
        await Login(
            _client,
            IntegrationTestEnvironment.BackofficeEmail,
            IntegrationTestEnvironment.BackofficePassword);
        using var logout = await _client.PostAsync("/api/v1/backoffice/auth/logout", null);
        using var revoked = await _client.PostAsync("/api/v1/backoffice/auth/refresh", null);

        using var noCookieClient = IntegrationTestEnvironment.Factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
                BaseAddress = new Uri("https://localhost")
            });
        using var unknownRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/backoffice/auth/logout");
        unknownRequest.Headers.Add("Cookie", "sarafan.backoffice.refresh=unknown-refresh-token");
        using var unknown = await noCookieClient.SendAsync(unknownRequest);
        using var unknownRefreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/backoffice/auth/refresh");
        unknownRefreshRequest.Headers.Add("Cookie", "sarafan.backoffice.refresh=unknown-refresh-token");
        using var unknownRefresh = await noCookieClient.SendAsync(unknownRefreshRequest);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(revoked.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(unknownRefresh.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
    }

    [Test]
    public async Task RepeatedLoginFailures_AreRateLimitedWithoutRevealingAccountState()
    {
        var email = NextEmail();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await _client.PostAsJsonAsync(
                "/api/v1/backoffice/auth/login",
                new BackofficeLoginRequest { Email = email, Password = "Wrong_backoffice_password" });
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }

        using var limited = await _client.PostAsJsonAsync(
            "/api/v1/backoffice/auth/login",
            new BackofficeLoginRequest { Email = email, Password = "Wrong_backoffice_password" });
        Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That((await ReadProblem(limited)).Code, Is.EqualTo("rate_limited"));
    }

    [Test]
    public async Task Administrator_CanManageUserAndSecurityChangesInvalidateSessions()
    {
        var administrator = await Login(
            _client,
            IntegrationTestEnvironment.BackofficeEmail,
            IntegrationTestEnvironment.BackofficePassword);
        var email = NextEmail();
        const string initialPassword = "Initial_operator_password";
        const string selfPassword = "Self_changed_password_13";

        using var create = await SendAuthorized(
            _client,
            HttpMethod.Post,
            "/api/v1/backoffice/users",
            administrator.AccessToken,
            JsonContent.Create(new BackofficeUserCreateRequest
            {
                Email = email,
                FirstName = "Olga",
                LastName = "Operator",
                Password = initialPassword,
                Roles = [BackofficeRoles.Operator]
            }));
        var created = await create.Content.ReadFromJsonAsync<BackofficeUserDto>();
        Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(created, Is.Not.Null);

        using var duplicate = await SendAuthorized(
            _client,
            HttpMethod.Post,
            "/api/v1/backoffice/users",
            administrator.AccessToken,
            JsonContent.Create(new BackofficeUserCreateRequest
            {
                Email = email.ToUpperInvariant(),
                FirstName = "Duplicate",
                LastName = "Operator",
                Password = initialPassword,
                Roles = [BackofficeRoles.Operator]
            }));
        Assert.That((await ReadProblem(duplicate)).Code, Is.EqualTo("backoffice_email_exists"));
        using var duplicateUpdate = await SendAuthorized(
            _client,
            HttpMethod.Put,
            $"/api/v1/backoffice/users/{created!.Id}",
            administrator.AccessToken,
            JsonContent.Create(new BackofficeUserUpdateRequest
            {
                Email = IntegrationTestEnvironment.BackofficeEmail,
                FirstName = "Olga",
                LastName = "Operator",
                IsActive = true,
                Roles = [BackofficeRoles.Operator]
            }));
        Assert.That((await ReadProblem(duplicateUpdate)).Code, Is.EqualTo("backoffice_email_exists"));

        var userSession = await Login(_client, email, initialPassword);
        using var ownRecord = await SendAuthorized(
            _client,
            HttpMethod.Get,
            $"/api/v1/backoffice/users/{created.Id}",
            userSession.AccessToken);
        var ownRecordJson = await ownRecord.Content.ReadAsStringAsync();
        using var otherRecord = await SendAuthorized(
            _client,
            HttpMethod.Get,
            $"/api/v1/backoffice/users/{administrator.User.Id}",
            userSession.AccessToken);
        using var forbiddenList = await SendAuthorized(
            _client,
            HttpMethod.Get,
            "/api/v1/backoffice/users",
            userSession.AccessToken);
        using var forbiddenRoles = await SendAuthorized(
            _client,
            HttpMethod.Get,
            "/api/v1/backoffice/roles",
            userSession.AccessToken);
        using var profileOnly = await SendAuthorized(
            _client,
            HttpMethod.Put,
            "/api/v1/backoffice/users/me",
            userSession.AccessToken,
            JsonContent.Create(new BackofficeSelfUpdateRequest
            {
                FirstName = "Olga",
                LastName = "Profile"
            }));
        using var stillValid = await SendAuthorized(
            _client,
            HttpMethod.Get,
            "/api/v1/backoffice/auth/me",
            userSession.AccessToken);
        using var selfUpdate = await SendAuthorized(
            _client,
            HttpMethod.Put,
            "/api/v1/backoffice/users/me",
            userSession.AccessToken,
            JsonContent.Create(new BackofficeSelfUpdateRequest
            {
                FirstName = "Olga",
                LastName = "Senior",
                Password = selfPassword
            }));
        using var staleAfterPassword = await SendAuthorized(
            _client,
            HttpMethod.Get,
            "/api/v1/backoffice/auth/me",
            userSession.AccessToken);
        using var oldPassword = await _client.PostAsJsonAsync(
            "/api/v1/backoffice/auth/login",
            new BackofficeLoginRequest { Email = email, Password = initialPassword });
        var changedSession = await Login(_client, email, selfPassword);

        using var update = await SendAuthorized(
            _client,
            HttpMethod.Put,
            $"/api/v1/backoffice/users/{created!.Id}",
            administrator.AccessToken,
            JsonContent.Create(new BackofficeUserUpdateRequest
            {
                Email = email,
                FirstName = "Olga",
                LastName = "Senior",
                IsActive = true,
                Roles = [BackofficeRoles.SeniorOperator]
            }));
        using var staleAfterRole = await SendAuthorized(
            _client,
            HttpMethod.Get,
            "/api/v1/backoffice/auth/me",
            changedSession.AccessToken);
        using var get = await SendAuthorized(
            _client,
            HttpMethod.Get,
            $"/api/v1/backoffice/users/{created.Id}",
            administrator.AccessToken);
        using var list = await SendAuthorized(
            _client,
            HttpMethod.Get,
            "/api/v1/backoffice/users",
            administrator.AccessToken);
        using var disable = await SendAuthorized(
            _client,
            HttpMethod.Delete,
            $"/api/v1/backoffice/users/{created.Id}",
            administrator.AccessToken);
        using var disableAgain = await SendAuthorized(
            _client,
            HttpMethod.Delete,
            $"/api/v1/backoffice/users/{created.Id}",
            administrator.AccessToken);
        using var disabledLogin = await _client.PostAsJsonAsync(
            "/api/v1/backoffice/auth/login",
            new BackofficeLoginRequest { Email = email, Password = selfPassword });
        const string resetPassword = "Administrator_reset_password_13";
        using var enable = await SendAuthorized(
            _client,
            HttpMethod.Put,
            $"/api/v1/backoffice/users/{created.Id}",
            administrator.AccessToken,
            JsonContent.Create(new BackofficeUserUpdateRequest
            {
                Email = email,
                FirstName = "Olga",
                LastName = "Senior",
                Password = resetPassword,
                IsActive = true,
                Roles = [BackofficeRoles.SeniorOperator]
            }));
        using var enabledLogin = await _client.PostAsJsonAsync(
            "/api/v1/backoffice/auth/login",
            new BackofficeLoginRequest { Email = email, Password = resetPassword });
        using var finalDisable = await SendAuthorized(
            _client,
            HttpMethod.Delete,
            $"/api/v1/backoffice/users/{created.Id}",
            administrator.AccessToken);

        var updated = await update.Content.ReadFromJsonAsync<BackofficeUserDto>();
        var fetched = await get.Content.ReadFromJsonAsync<BackofficeUserDto>();
        var users = await list.Content.ReadFromJsonAsync<List<BackofficeUserDto>>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(forbiddenList.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(forbiddenRoles.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(ownRecord.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(JsonSerializer.Deserialize<BackofficeIdentityDto>(ownRecordJson, JsonOptions()), Is.Not.Null);
            Assert.That(ownRecordJson, Does.Not.Contain("isActive").And.Not.Contain("isDemo").And.Not.Contain("createdAt"));
            Assert.That(otherRecord.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(profileOnly.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stillValid.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(selfUpdate.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(staleAfterPassword.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(oldPassword.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(updated?.Roles, Is.EqualTo(new[] { BackofficeRoles.SeniorOperator }));
            Assert.That(staleAfterRole.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(fetched?.LastName, Is.EqualTo("Senior"));
            Assert.That(users?.Any(item => item.Id == created.Id), Is.True);
            Assert.That(disable.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(disableAgain.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(disabledLogin.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(enable.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(enabledLogin.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(finalDisable.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }
    }

    [Test]
    public async Task InvalidRolesAndMissingUsersUseStableProblems()
    {
        var administrator = await Login(
            _client,
            IntegrationTestEnvironment.BackofficeEmail,
            IntegrationTestEnvironment.BackofficePassword);
        using var invalidRole = await SendAuthorized(
            _client,
            HttpMethod.Post,
            "/api/v1/backoffice/users",
            administrator.AccessToken,
            JsonContent.Create(new BackofficeUserCreateRequest
            {
                Email = NextEmail(),
                FirstName = "Invalid",
                LastName = "Role",
                Password = "Valid_test_password_13",
                Roles = ["unknown-role"]
            }));
        using var ops = await SendAuthorized(
            _client,
            HttpMethod.Get,
            "/api/v1/backoffice/users/ops",
            administrator.AccessToken);
        using var missingGet = await SendAuthorized(
            _client,
            HttpMethod.Get,
            "/api/v1/backoffice/users/2147483647",
            administrator.AccessToken);
        using var emptyRoles = await SendAuthorized(
            _client,
            HttpMethod.Post,
            "/api/v1/backoffice/users",
            administrator.AccessToken,
            JsonContent.Create(new BackofficeUserCreateRequest
            {
                Email = NextEmail(),
                FirstName = "Empty",
                LastName = "Roles",
                Password = "Valid_test_password_13",
                Roles = []
            }));
        using var oversizedUtf8Password = await SendAuthorized(
            _client,
            HttpMethod.Post,
            "/api/v1/backoffice/users",
            administrator.AccessToken,
            JsonContent.Create(new BackofficeUserCreateRequest
            {
                Email = NextEmail(),
                FirstName = "Invalid",
                LastName = "Password",
                Password = new string('я', 40),
                Roles = [BackofficeRoles.Operator]
            }));
        using var missingDelete = await SendAuthorized(
            _client,
            HttpMethod.Delete,
            "/api/v1/backoffice/users/2147483647",
            administrator.AccessToken);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((await ReadProblem(invalidRole)).Code, Is.EqualTo("invalid_backoffice_role"));
            Assert.That(ops.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await ReadProblem(emptyRoles)).Code, Is.EqualTo("invalid_backoffice_role"));
            Assert.That((await ReadProblem(oversizedUtf8Password)).Code, Is.EqualTo("invalid_backoffice_user_data"));
            Assert.That((await ReadProblem(missingGet)).Code, Is.EqualTo("backoffice_user_not_found"));
            Assert.That((await ReadProblem(missingDelete)).Code, Is.EqualTo("backoffice_user_not_found"));
        }
    }

    [Test]
    public async Task LastActiveAdministrator_CannotBeDemotedOrDisabled()
    {
        var administrator = await Login(
            _client,
            IntegrationTestEnvironment.BackofficeEmail,
            IntegrationTestEnvironment.BackofficePassword);
        using var demote = await SendAuthorized(
            _client,
            HttpMethod.Put,
            $"/api/v1/backoffice/users/{administrator.User.Id}",
            administrator.AccessToken,
            JsonContent.Create(new BackofficeUserUpdateRequest
            {
                Email = administrator.User.Email,
                FirstName = administrator.User.FirstName,
                LastName = administrator.User.LastName,
                IsActive = true,
                Roles = [BackofficeRoles.Operator]
            }));
        using var disable = await SendAuthorized(
            _client,
            HttpMethod.Delete,
            $"/api/v1/backoffice/users/{administrator.User.Id}",
            administrator.AccessToken);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((await ReadProblem(demote)).Code, Is.EqualTo("last_backoffice_administrator"));
            Assert.That((await ReadProblem(disable)).Code, Is.EqualTo("last_backoffice_administrator"));
        }

        using var createSecond = await SendAuthorized(
            _client,
            HttpMethod.Post,
            "/api/v1/backoffice/users",
            administrator.AccessToken,
            JsonContent.Create(new BackofficeUserCreateRequest
            {
                Email = NextEmail(),
                FirstName = "Second",
                LastName = "Administrator",
                Password = "Second_admin_password_13",
                Roles = [BackofficeRoles.Administrator]
            }));
        var second = await createSecond.Content.ReadFromJsonAsync<BackofficeUserDto>();
        using var disableSecond = await SendAuthorized(
            _client,
            HttpMethod.Delete,
            $"/api/v1/backoffice/users/{second!.Id}",
            administrator.AccessToken);
        Assert.That(disableSecond.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    [Test]
    public async Task ConcurrentDuplicateEmails_CreateExactlyOneUser()
    {
        var administrator = await Login(
            _client,
            IntegrationTestEnvironment.BackofficeEmail,
            IntegrationTestEnvironment.BackofficePassword);
        var email = NextEmail();
        var first = SendAuthorized(
            _client,
            HttpMethod.Post,
            "/api/v1/backoffice/users",
            administrator.AccessToken,
            JsonContent.Create(new BackofficeUserCreateRequest
            {
                Email = email,
                FirstName = "Concurrent",
                LastName = "First",
                Password = "Concurrent_email_password_13",
                Roles = [BackofficeRoles.Operator]
            }));
        var second = SendAuthorized(
            _client,
            HttpMethod.Post,
            "/api/v1/backoffice/users",
            administrator.AccessToken,
            JsonContent.Create(new BackofficeUserCreateRequest
            {
                Email = email.ToUpperInvariant(),
                FirstName = "Concurrent",
                LastName = "Second",
                Password = "Concurrent_email_password_13",
                Roles = [BackofficeRoles.Operator]
            }));

        var responses = await Task.WhenAll(first, second);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(responses.Count(item => item.StatusCode == HttpStatusCode.Created), Is.EqualTo(1));
                Assert.That(responses.Count(item => item.StatusCode == HttpStatusCode.Conflict), Is.EqualTo(1));
            }

            var createdResponse = responses.Single(item => item.StatusCode == HttpStatusCode.Created);
            var created = (await createdResponse.Content.ReadFromJsonAsync<BackofficeUserDto>())!;
            using var disable = await SendAuthorized(
                _client,
                HttpMethod.Delete,
                $"/api/v1/backoffice/users/{created.Id}",
                administrator.AccessToken);
            Assert.That(disable.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
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
    public async Task MissingBackofficeToken_ReturnsDedicatedChallenge()
    {
        using var response = await _client.GetAsync("/api/v1/backoffice/auth/me");
        var problem = await ReadProblem(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Headers.WwwAuthenticate.Single().Scheme, Is.EqualTo("Bearer"));
            Assert.That(problem.Code, Is.EqualTo("invalid_backoffice_access_token"));
        }
    }

    [Test]
    public async Task Swagger_DeclaresDedicatedBackofficeSchemeAndRoutes()
    {
        using var response = await _client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var schemes = root.GetProperty("components").GetProperty("securitySchemes");
        var paths = root.GetProperty("paths");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(schemes.TryGetProperty("Bearer", out _), Is.True);
            Assert.That(schemes.TryGetProperty("BackofficeBearer", out var backoffice), Is.True);
            Assert.That(backoffice.GetProperty("scheme").GetString(), Is.EqualTo("bearer"));
            Assert.That(paths.TryGetProperty("/api/v1/backoffice/auth/login", out _), Is.True);
            Assert.That(paths.TryGetProperty("/api/v1/backoffice/users/ops", out _), Is.True);
            Assert.That(paths.TryGetProperty("/api/v1/backoffice/roles", out _), Is.True);
        }
    }

    private static async Task<BackofficeAuthenticationSessionDto> Login(
        HttpClient client,
        string email,
        string password)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/backoffice/auth/login",
            new BackofficeLoginRequest { Email = email, Password = password });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<BackofficeAuthenticationSessionDto>())!;
    }

    private static async Task<HttpResponseMessage> SendAuthorized(
        HttpClient client,
        HttpMethod method,
        string path,
        string token,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<SarafanProblemDetails> ReadProblem(HttpResponseMessage response)
    {
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(SarafanProblemDetailsFactory.MediaType));
        return (await response.Content.ReadFromJsonAsync<SarafanProblemDetails>())!;
    }

    private static string ExtractRefreshCookie(HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("sarafan.backoffice.refresh=", StringComparison.Ordinal));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cookie, Does.Contain("path=/api/v1/backoffice/auth").IgnoreCase);
            Assert.That(cookie, Does.Contain("httponly").IgnoreCase);
            Assert.That(cookie, Does.Contain("samesite=strict").IgnoreCase);
        }

        return cookie[..cookie.IndexOf(';')];
    }

    private static string NextEmail()
        => $"staff-{Interlocked.Increment(ref _emailSequence)}@sarafan.test";

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web);
}
