// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Sarafan.Core.Authentication;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[TestFixture]
[NonParallelizable]
public sealed class ProblemDetailsContractTests
{
    private static int _phoneSequence = 8_000_000;
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
    public async Task AutomaticValidation_ReturnsStructuredRussianProblem()
    {
        using var content = new StringContent(
            """{"phone":"","purpose":"other"}""",
            Encoding.UTF8,
            "application/json");
        using var response = await _client.PostAsync("/api/v1/auth/code/request", content);
        var problem = await ReadProblem(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem.Status, Is.EqualTo(400));
            Assert.That(problem.Code, Is.EqualTo("validation_failed"));
            Assert.That(problem.Type, Is.EqualTo(
                "https://sarafan.sw.consulting/problems/validation-failed"));
            Assert.That(problem.Instance, Does.StartWith("urn:sarafan:problem:"));
            Assert.That(problem.TraceId, Is.Not.Empty);
            Assert.That(response.Content.Headers.ContentLanguage, Does.Contain("ru"));
            Assert.That(problem.Errors?.Keys, Does.Contain("phone"));
            Assert.That(problem.Errors?.Keys, Does.Contain("purpose"));
            Assert.That(problem.Errors?["phone"], Does.Contain("Поле обязательно для заполнения."));
            Assert.That(problem.Errors?["purpose"], Does.Contain("Укажите register или login."));
            Assert.That(problem.Errors?.SelectMany(item => item.Value),
                Is.All.Matches<string>(value => Regex.IsMatch(value, "[А-Яа-яЁё]")));
        }
    }

    [Test]
    public async Task AuthenticationChallenge_ReturnsAccessTokenProblem()
    {
        using var response = await _client.GetAsync("/api/v1/customers/me");
        var problem = await ReadProblem(response);
        var challenge = response.Headers.WwwAuthenticate.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(challenge.Scheme, Is.EqualTo("Bearer"));
            Assert.That(challenge.Parameter, Is.Null);
            Assert.That(problem.Status, Is.EqualTo(401));
            Assert.That(problem.Code, Is.EqualTo("invalid_access_token"));
            Assert.That(problem.Type, Is.EqualTo(
                "https://sarafan.sw.consulting/problems/invalid-access-token"));
        }
    }

    [Test]
    public async Task InvalidAccessToken_ReturnsSafeBearerErrorChallenge()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/customers/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");
        using var response = await _client.SendAsync(request);
        var problem = await ReadProblem(response);
        var challenge = response.Headers.WwwAuthenticate.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(challenge.Scheme, Is.EqualTo("Bearer"));
            Assert.That(challenge.Parameter, Is.EqualTo("error=\"invalid_token\""));
            Assert.That(problem.Code, Is.EqualTo("invalid_access_token"));
        }
    }

    [Test]
    public async Task JwtEvents_ReturnRussianChallengeAndForbiddenProblems()
    {
        var events = new SarafanJwtBearerEvents();
        var scheme = JwtScheme();
        var options = new JwtBearerOptions();
        var challengeContext = ContextWithFactory("/challenge");
        var forbiddenContext = ContextWithFactory("/forbidden");

        await events.Challenge(new JwtBearerChallengeContext(
            challengeContext,
            scheme,
            options,
            new AuthenticationProperties()));
        await events.Forbidden(new ForbiddenContext(forbiddenContext, scheme, options));

        var challenge = JsonSerializer.Deserialize<SarafanProblemDetails>(
            await Body(challengeContext),
            JsonOptions())!;
        var forbidden = JsonSerializer.Deserialize<SarafanProblemDetails>(
            await Body(forbiddenContext),
            JsonOptions())!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(challengeContext.Response.StatusCode, Is.EqualTo(401));
            Assert.That(challengeContext.Response.Headers.WWWAuthenticate.ToString(), Is.EqualTo("Bearer"));
            Assert.That(challenge.Code, Is.EqualTo("invalid_access_token"));
            Assert.That(forbiddenContext.Response.StatusCode, Is.EqualTo(403));
            Assert.That(forbidden.Code, Is.EqualTo("access_denied"));
            Assert.That(forbiddenContext.Response.Headers.ContentLanguage.ToString(), Is.EqualTo("ru"));
        }
    }

    [Test]
    public async Task JwtEvent_InvalidTokenChallenge_PreservesConfiguredRealmWithoutDetails()
    {
        var context = ContextWithFactory("/invalid-token");
        var options = new JwtBearerOptions { Challenge = "Bearer realm=\"sarafan\"" };
        var challenge = new JwtBearerChallengeContext(
            context,
            JwtScheme(),
            options,
            new AuthenticationProperties())
        {
            AuthenticateFailure = new InvalidOperationException("sensitive validation detail")
        };

        await new SarafanJwtBearerEvents().Challenge(challenge);

        Assert.That(
            context.Response.Headers.WWWAuthenticate.ToString(),
            Is.EqualTo("Bearer realm=\"sarafan\", error=\"invalid_token\""));
        Assert.That(
            context.Response.Headers.WWWAuthenticate.ToString(),
            Does.Not.Contain("sensitive validation detail"));
    }

    [Test]
    public async Task UnknownRoute_ReturnsResourceNotFoundProblem()
    {
        using var response = await _client.GetAsync("/api/v1/does-not-exist?source=test");
        var problem = await ReadProblem(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(problem.Status, Is.EqualTo(404));
            Assert.That(problem.Code, Is.EqualTo("resource_not_found"));
            Assert.That(problem.Instance, Does.StartWith("urn:sarafan:problem:"));
        }
    }

    [Test]
    public async Task UnsupportedMediaType_ReturnsProblemInsteadOfEmptyStatus()
    {
        using var content = new StringContent("phone=123", Encoding.UTF8, "text/plain");
        using var response = await _client.PostAsync("/api/v1/auth/code/request", content);
        var problem = await ReadProblem(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnsupportedMediaType));
            Assert.That(problem.Status, Is.EqualTo(415));
            Assert.That(problem.Code, Is.EqualTo("unsupported_media_type"));
        }
    }

    [Test]
    public async Task DomainFailure_ReturnsCatalogProblemWithoutExceptionMessage()
    {
        using var content = new StringContent(
            """{"phone":"+79990009999","purpose":"register","code":"2222","termsAccepted":true,"personalDataAccepted":true}""",
            Encoding.UTF8,
            "application/json");
        using var response = await _client.PostAsync("/api/v1/auth/code/verify", content);
        var json = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<SarafanProblemDetails>(json, JsonOptions())!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(problem.Status, Is.EqualTo(401));
            Assert.That(problem.Code, Is.EqualTo("invalid_code"));
            Assert.That(problem.Title, Does.Match("[А-Яа-яЁё]"));
            Assert.That(problem.Detail, Does.Match("[А-Яа-яЁё]"));
            Assert.That(json, Does.Not.Contain("ServiceException"));
            Assert.That(json, Does.Not.Contain("stack"));
        }
    }

    [Test]
    public async Task RefreshWithoutCookie_ReturnsInvalidRefreshTokenProblem()
    {
        using var response = await _client.PostAsync("/api/v1/auth/refresh", content: null);
        var problem = await ReadProblem(response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(problem.Code, Is.EqualTo("invalid_refresh_token"));
        }
    }

    [Test]
    public async Task AuthenticationServiceFailures_UseCatalogProblems()
    {
        var phone = NextPhone();
        using var invalidPhone = await _client.PostAsync(
            "/api/v1/auth/code/request",
            new StringContent(
                """{"phone":"not-a-phone","purpose":"register"}""",
                Encoding.UTF8,
                "application/json"));
        using var failedLogin = await _client.PostAsync(
            "/api/v1/auth/code/verify",
            new StringContent(
                $$"""{"phone":"{{phone}}","purpose":"login","code":"{{VerificationCode(phone)}}"}""",
                Encoding.UTF8,
                "application/json"));
        var invalidPhoneProblem = await ReadProblem(invalidPhone);
        var failedLoginProblem = await ReadProblem(failedLogin);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(invalidPhoneProblem.Code, Is.EqualTo("invalid_phone"));
            Assert.That(failedLoginProblem.Code, Is.EqualTo("login_failed"));
        }
    }

    [Test]
    public async Task PhotoValidation_UsesNamedCatalogProblems()
    {
        var session = await Register(NextPhone());
        using var emptyForm = new MultipartFormDataContent();
        using var emptyContent = new ByteArrayContent([]);
        emptyContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        emptyForm.Add(emptyContent, "file", "empty.png");
        using var emptyRequest = AuthorizedRequest(
            HttpMethod.Put,
            "/api/v1/customers/me/photo",
            session.AccessToken,
            emptyForm);
        using var emptyResponse = await _client.SendAsync(emptyRequest);
        var emptyProblem = await ReadProblem(emptyResponse);

        using var gifForm = new MultipartFormDataContent();
        using var gifContent = new ByteArrayContent([1]);
        gifContent.Headers.ContentType = new MediaTypeHeaderValue("image/gif");
        gifForm.Add(gifContent, "file", "avatar.gif");
        using var gifRequest = AuthorizedRequest(
            HttpMethod.Put,
            "/api/v1/customers/me/photo",
            session.AccessToken,
            gifForm);
        using var gifResponse = await _client.SendAsync(gifRequest);
        var gifProblem = await ReadProblem(gifResponse);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(emptyProblem.Code, Is.EqualTo("invalid_photo_size"));
            Assert.That(gifProblem.Code, Is.EqualTo("invalid_photo_type"));
        }
    }

    [Test]
    public async Task ExceptionHandler_UsesSafeProblemForUnexpectedException()
    {
        var factory = new SarafanProblemDetailsFactory();
        var handler = new SarafanExceptionHandler(
            factory,
            NullLogger<SarafanExceptionHandler>.Instance);
        var context = Context("/explode");

        var handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException("database-password-is-secret"),
            default);
        var json = await Body(context);
        var problem = JsonSerializer.Deserialize<SarafanProblemDetails>(json, JsonOptions())!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handled, Is.True);
            Assert.That(context.Response.StatusCode, Is.EqualTo(500));
            Assert.That(problem.Code, Is.EqualTo("internal_error"));
            Assert.That(problem.Status, Is.EqualTo(500));
            Assert.That(json, Does.Not.Contain("database-password-is-secret"));
        }
    }

    [Test]
    public async Task ExceptionHandler_PreservesKnownServiceStatusAndCode()
    {
        var factory = new SarafanProblemDetailsFactory();
        var handler = new SarafanExceptionHandler(
            factory,
            NullLogger<SarafanExceptionHandler>.Instance);
        var context = Context("/account");

        var handled = await handler.TryHandleAsync(
            context,
            new ServiceException(409, "account_exists"),
            default);
        var problem = JsonSerializer.Deserialize<SarafanProblemDetails>(await Body(context), JsonOptions())!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handled, Is.True);
            Assert.That(problem.Code, Is.EqualTo("account_exists"));
            Assert.That(problem.Status, Is.EqualTo(409));
        }
    }

    [Test]
    public async Task ExceptionHandler_MapsBadHttpRequestStatus()
    {
        var handler = new SarafanExceptionHandler(
            new SarafanProblemDetailsFactory(),
            NullLogger<SarafanExceptionHandler>.Instance);
        var context = Context("/too-large");

        var handled = await handler.TryHandleAsync(
            context,
            new BadHttpRequestException("sensitive parser detail", StatusCodes.Status413PayloadTooLarge),
            default);
        var problem = JsonSerializer.Deserialize<SarafanProblemDetails>(await Body(context), JsonOptions())!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handled, Is.True);
            Assert.That(problem.Status, Is.EqualTo(413));
            Assert.That(problem.Code, Is.EqualTo("request_too_large"));
        }
    }

    [Test]
    public async Task ExceptionHandler_DoesNotReplaceStartedResponse()
    {
        var handler = new SarafanExceptionHandler(
            new SarafanProblemDetailsFactory(),
            NullLogger<SarafanExceptionHandler>.Instance);
        var context = Context("/started");
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature(context.Response.Body));

        var handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException(),
            default);

        Assert.That(handled, Is.False);
    }

    [Test]
    public void ControllerHelpers_DelegateEveryProblemToCentralFactory()
    {
        var context = Context("/controller");
        var controller = new TestController(new SarafanProblemDetailsFactory())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var expected = new Dictionary<string, int>
        {
            ["invalid_refresh_token"] = 401,
            ["invalid_backoffice_refresh_token"] = 401,
            ["access_denied"] = 403,
            ["customer_not_found"] = 404,
            ["photo_not_found"] = 404,
            ["invalid_photo_size"] = 400,
            ["invalid_photo_type"] = 400,
            ["invalid_photo_content"] = 400
        };

        foreach (var (code, status) in expected)
        {
            var result = (ObjectResult)controller.Problem(code);
            var problem = (SarafanProblemDetails)result.Value!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.StatusCode, Is.EqualTo(status));
                Assert.That(result.ContentTypes, Does.Contain(SarafanProblemDetailsFactory.MediaType));
                Assert.That(problem.Code, Is.EqualTo(code));
            }
        }
    }

    [Test]
    public void ControllerIdentityHelpers_UseClaimsAndSafeAddressFallback()
    {
        var context = Context("/controller");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(JwtRegisteredClaimNames.Sub, "42") },
            "test"));
        var controller = new TestController(new SarafanProblemDetailsFactory())
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(controller.CustomerId(), Is.EqualTo(42));
            Assert.That(controller.BackofficeUserId(), Is.EqualTo(42));
            Assert.That(controller.Address(), Is.EqualTo("unknown"));
        }

        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.That(controller.Address(), Is.EqualTo("127.0.0.1"));
    }

    [Test]
    public void ControllerIdentityHelper_RejectsMissingCustomerClaimWithoutClientText()
    {
        var controller = new TestController(new SarafanProblemDetailsFactory())
        {
            ControllerContext = new ControllerContext { HttpContext = Context("/controller") }
        };

        var exception = Assert.Throws<ServiceException>(() => controller.CustomerId());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(401));
            Assert.That(exception.Code, Is.EqualTo("invalid_access_token"));
        }
    }

    [Test]
    public void ControllerIdentityHelper_RejectsMissingBackofficeClaimWithoutClientText()
    {
        var controller = new TestController(new SarafanProblemDetailsFactory())
        {
            ControllerContext = new ControllerContext { HttpContext = Context("/controller") }
        };

        var exception = Assert.Throws<ServiceException>(() => controller.BackofficeUserId());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(401));
            Assert.That(exception.Code, Is.EqualTo("invalid_backoffice_access_token"));
        }
    }

    [Test]
    public async Task Swagger_DeclaresProblemMediaTypeAndSchema()
    {
        using var response = await _client.GetAsync("/swagger/v1/swagger.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var problemContent = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v1/auth/code/request")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("400")
            .GetProperty("content")
            .GetProperty(SarafanProblemDetailsFactory.MediaType);

        Assert.That(
            problemContent.GetProperty("schema").GetProperty("$ref").GetString(),
            Does.EndWith("/SarafanProblemDetails"));
    }

    [TestCase(400, "bad_request")]
    [TestCase(401, "invalid_access_token")]
    [TestCase(403, "access_denied")]
    [TestCase(404, "resource_not_found")]
    [TestCase(405, "method_not_allowed")]
    [TestCase(413, "request_too_large")]
    [TestCase(415, "unsupported_media_type")]
    [TestCase(429, "rate_limited")]
    [TestCase(500, "internal_error")]
    [TestCase(503, "service_unavailable")]
    [TestCase(418, "internal_error")]
    public void CodeForStatus_ReturnsStableCode(int status, string expected)
        => Assert.That(SarafanProblemDetailsFactory.CodeForStatus(status), Is.EqualTo(expected));

    [Test]
    public void Catalog_ProvidesRussianSafeTextForEveryDeclaredProblem()
    {
        var codes = new Dictionary<string, int>
        {
            ["validation_failed"] = 400,
            ["invalid_phone"] = 400,
            ["invalid_purpose"] = 400,
            ["consent_required"] = 400,
            ["invalid_photo_size"] = 400,
            ["invalid_photo_type"] = 400,
            ["invalid_photo_content"] = 400,
            ["invalid_code"] = 401,
            ["invalid_access_token"] = 401,
            ["invalid_refresh_token"] = 401,
            ["invalid_backoffice_access_token"] = 401,
            ["invalid_backoffice_refresh_token"] = 401,
            ["login_failed"] = 401,
            ["backoffice_login_failed"] = 401,
            ["access_denied"] = 403,
            ["customer_not_found"] = 404,
            ["photo_not_found"] = 404,
            ["backoffice_user_not_found"] = 404,
            ["account_exists"] = 409,
            ["backoffice_email_exists"] = 409,
            ["last_backoffice_administrator"] = 409,
            ["demo_backoffice_forbidden"] = 409,
            ["invalid_backoffice_role"] = 400,
            ["invalid_backoffice_user_data"] = 400,
            ["rate_limited"] = 429,
            ["internal_error"] = 500,
            ["verification_unavailable"] = 503,
            ["resource_not_found"] = 404,
            ["method_not_allowed"] = 405,
            ["request_too_large"] = 413,
            ["unsupported_media_type"] = 415,
            ["bad_request"] = 400,
            ["service_unavailable"] = 503
        };
        var factory = new SarafanProblemDetailsFactory();
        var context = Context("/catalog");

        foreach (var (code, status) in codes)
        {
            var problem = factory.Create(context, status, code);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(problem.Type, Is.EqualTo(
                    $"{SarafanProblemDetailsFactory.TypeBase}{code.Replace('_', '-')}"));
                Assert.That(problem.Code, Is.EqualTo(code));
                Assert.That(problem.Status, Is.EqualTo(status));
                Assert.That(problem.Title, Does.Match("[А-Яа-яЁё]"));
                Assert.That(problem.Detail, Does.Match("[А-Яа-яЁё]"));
            }
        }
    }

    [Test]
    public void UnknownCatalogCode_IsNormalizedToInternalError()
    {
        var problem = new SarafanProblemDetailsFactory().Create(Context("/unknown"), 418, "secret_db_error");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Code, Is.EqualTo("internal_error"));
            Assert.That(problem.Status, Is.EqualTo(500));
            Assert.That(problem.Type, Is.EqualTo(
                "https://sarafan.sw.consulting/problems/internal-error"));
        }
    }

    [Test]
    public void StatusMismatch_IsNormalizedToInternalError()
    {
        var problem = new SarafanProblemDetailsFactory().Create(
            Context("/mismatch"),
            400,
            "account_exists");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Code, Is.EqualTo("internal_error"));
            Assert.That(problem.Status, Is.EqualTo(500));
        }
    }

    [Test]
    public void Validation_PreservesRussianMessagesAndSanitizesFrameworkText()
    {
        var context = Context("/validation");
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("Localized", "Укажите корректное значение.");
        modelState.AddModelError("Framework", "The JSON value could not be converted.");

        var result = new SarafanProblemDetailsFactory().CreateValidationResult(context, modelState);
        var problem = (SarafanProblemDetails)result.Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Errors?["localized"], Is.EqualTo(new[] { "Укажите корректное значение." }));
            Assert.That(problem.Errors?["framework"], Is.EqualTo(new[] { "Значение заполнено некорректно." }));
            Assert.That(context.Response.Headers.ContentLanguage.ToString(), Is.EqualTo("ru"));
        }
    }

    [Test]
    public void Controllers_UseCentralizedProblemConstructionOnly()
    {
        var root = RepositoryRoot();
        var baseController = Path.Combine(
            root,
            "src",
            "Sarafan.Core",
            "Controllers",
            "SarafanControllerBase.cs");
        var controllers = Directory.GetFiles(Path.GetDirectoryName(baseController)!, "*.cs")
            .Where(path => !string.Equals(path, baseController, StringComparison.OrdinalIgnoreCase));
        var forbiddenResult = new Regex(
            @"\b(?:BadRequest|NotFound|Unauthorized|Forbid|Conflict|Problem|StatusCode)\s*\(",
            RegexOptions.CultureInvariant);

        foreach (var controller in controllers)
        {
            var source = File.ReadAllText(controller);
            Assert.That(forbiddenResult.IsMatch(source), Is.False, Path.GetFileName(controller));
            Assert.That(source, Does.Not.Contain("new SarafanProblemDetails"), Path.GetFileName(controller));
            Assert.That(source, Does.Not.Contain("new ProblemDetails"), Path.GetFileName(controller));
        }
    }

    [Test]
    public void Source_RestrictsProblemPayloadConstructionAndServiceSignals()
    {
        var sourceRoot = Path.Combine(RepositoryRoot(), "src", "Sarafan.Core");
        var factoryPath = Path.Combine(sourceRoot, "Services", "SarafanProblemDetailsFactory.cs");
        var modelPath = Path.Combine(sourceRoot, "RestModels", "SarafanProblemDetails.cs");
        var serviceExceptionPattern = new Regex(
            @"new\s+ServiceException\s*\([^\)]*,[^\)]*,",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        foreach (var path in Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(path);
            if (!string.Equals(path, factoryPath, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                Assert.That(source, Does.Not.Contain("new SarafanProblemDetails"), path);
                Assert.That(source, Does.Not.Contain("new ProblemDetails"), path);
            }

            Assert.That(serviceExceptionPattern.IsMatch(source), Is.False, path);
        }
    }

    private static async Task<SarafanProblemDetails> ReadProblem(HttpResponseMessage response)
    {
        Assert.That(
            response.Content.Headers.ContentType?.MediaType,
            Is.EqualTo(SarafanProblemDetailsFactory.MediaType));
        var payload = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<SarafanProblemDetails>(payload, JsonOptions())!;
    }

    private async Task<AuthenticationSessionDto> Register(string phone)
    {
        using var requestCode = await _client.PostAsync(
            "/api/v1/auth/code/request",
            new StringContent(
                $$"""{"phone":"{{phone}}","purpose":"register"}""",
                Encoding.UTF8,
                "application/json"));
        Assert.That(requestCode.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        using var verify = await _client.PostAsync(
            "/api/v1/auth/code/verify",
            new StringContent(
                $$"""{"phone":"{{phone}}","purpose":"register","code":"{{VerificationCode(phone)}}","termsAccepted":true,"personalDataAccepted":true}""",
                Encoding.UTF8,
                "application/json"));
        Assert.That(verify.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await JsonSerializer.DeserializeAsync<AuthenticationSessionDto>(
            await verify.Content.ReadAsStreamAsync(),
            JsonOptions()))!;
    }

    private static HttpRequestMessage AuthorizedRequest(
        HttpMethod method,
        string path,
        string token,
        HttpContent content)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static string NextPhone()
        => $"+7999{Interlocked.Increment(ref _phoneSequence):D7}";

    private static string VerificationCode(string phone) => phone[^4..];

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web);

    private static DefaultHttpContext Context(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "test-trace";
        return context;
    }

    private static DefaultHttpContext ContextWithFactory(string path)
    {
        var context = Context(path);
        context.RequestServices = new ServiceCollection()
            .AddSingleton(new SarafanProblemDetailsFactory())
            .BuildServiceProvider();
        return context;
    }

    private static AuthenticationScheme JwtScheme() => new(
        JwtBearerDefaults.AuthenticationScheme,
        displayName: null,
        typeof(JwtBearerHandler));

    private static async Task<string> Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Sarafan.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class TestController(SarafanProblemDetailsFactory factory)
        : Sarafan.Core.Controllers.SarafanControllerBase(factory)
    {
        public ActionResult Problem(string code) => code switch
        {
            "invalid_refresh_token" => InvalidRefreshTokenProblem(),
            "invalid_backoffice_refresh_token" => InvalidBackofficeRefreshTokenProblem(),
            "access_denied" => AccessDeniedProblem(),
            "customer_not_found" => CustomerNotFoundProblem(),
            "photo_not_found" => PhotoNotFoundProblem(),
            "invalid_photo_size" => InvalidPhotoSizeProblem(),
            "invalid_photo_type" => InvalidPhotoTypeProblem(),
            "invalid_photo_content" => InvalidPhotoContentProblem(),
            _ => throw new ArgumentOutOfRangeException(nameof(code))
        };

        public int CustomerId() => CurrentCustomerId();

        public int BackofficeUserId() => CurrentBackofficeUserId();

        public string Address() => RemoteAddress();
    }

    private sealed class StartedResponseFeature(Stream body) : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = body;
        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }
}
