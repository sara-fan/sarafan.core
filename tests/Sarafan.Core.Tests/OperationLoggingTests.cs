// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sarafan.Core.Authentication;
using Sarafan.Core.Controllers;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[TestFixture]
[NonParallelizable]
public sealed class OperationLoggingTests
{
    private const string Secret = "private-value-must-not-appear";
    private const string Operation = "Sarafan.Core.Services.Example.Execute";
    private LogCollector _logs = null!;
    private ILoggerFactory _factory = null!;
    private ILogger<OperationLoggingTests> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _logs = new LogCollector();
        _factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(_logs));
        _logger = _factory.CreateLogger<OperationLoggingTests>();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public void SynchronousOperation_LogsStableEventsSafeValuesAndTrace()
    {
        using var activity = new Activity("logging-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        var result = OperationLogging.Run(_logger, Operation,
            () => LogValueSummary.Inputs(("request", new RequestCodeRequest { Phone = Secret, Purpose = "login" })),
            () => true);

        Assert.That(result, Is.True);
        var records = _logs.Records.ToArray();
        Assert.That(records.Select(record => record.Event.Id), Is.EqualTo(new[] { 1600, 1601 }));
        Assert.That(records.Select(record => record.Event.Name), Is.EqualTo(new[]
        {
            SarafanEvents.OperationEnteredName, SarafanEvents.OperationExitedName
        }));
        Assert.That(records.Select(record => record.Level), Is.All.EqualTo(LogLevel.Debug));
        Assert.That(records[0].Message, Is.EqualTo($"Entering {Operation}. Inputs: request=RequestCodeRequest(purpose=login; phone=[redacted])."));
        Assert.That(records[1].Message, Is.EqualTo($"Exiting {Operation}. Outputs: true."));
        Assert.That(records[0].Attributes["sarafan.operation.inputs"], Does.Contain("purpose=login"));
        Assert.That(records[1].Attributes["sarafan.operation.outputs"], Is.EqualTo("true"));
        Assert.That(records.Select(record => record.Attributes["code.function.name"]), Is.All.EqualTo(Operation));
        Assert.That(records.Select(record => record.TraceId), Is.All.EqualTo(activity.TraceId.ToHexString()));
        Assert.That(records.Select(record => record.SpanId), Is.All.EqualTo(activity.SpanId.ToHexString()));
        AssertPrivate();
    }

    [Test]
    public void SynchronousFailure_PreservesExceptionAndStackAndLogsWarning()
    {
        var failure = new InvalidOperationException(Secret);
        var thrown = Assert.Throws<InvalidOperationException>(() => OperationLogging.Run<bool>(
            _logger, Operation, () => "none", () => ThrowFromOperation(failure)));

        Assert.That(thrown, Is.SameAs(failure));
        Assert.That(thrown!.StackTrace, Does.Contain(nameof(ThrowFromOperation)));
        AssertFailure(failure, true, "unexpected exception");
    }

    [TestCase("service", false, "service rejection; status=401")]
    [TestCase("request", false, "request rejection; status=413")]
    [TestCase("cancelled", false, "cancelled")]
    [TestCase("unsolicited", true, "unexpected exception")]
    [TestCase("unexpected", true, "unexpected exception")]
    public void AsyncFailure_LogsCorrectSeverityAndPreservesException(string kind, bool warning, string output)
    {
        using var cancellation = new CancellationTokenSource();
        if (kind == "cancelled")
        {
            cancellation.Cancel();
        }

        Exception failure = kind switch
        {
            "service" => new ServiceException(401, Secret),
            "request" => new BadHttpRequestException(Secret, 413),
            "cancelled" or "unsolicited" => new OperationCanceledException(Secret),
            _ => new InvalidOperationException(Secret)
        };

        var thrown = Assert.ThrowsAsync(failure.GetType(), () => OperationLogging.RunAsync<bool>(
            _logger, Operation, () => "none", async () =>
            {
                await Task.Yield();
                throw failure;
            }, cancellation.Token));

        Assert.That(thrown, Is.SameAs(failure));
        AssertFailure(failure, warning, output);
    }

    [Test]
    public async Task AsyncSuccessAndVoid_EachLogOneOutput()
    {
        var result = await OperationLogging.RunAsync(_logger, Operation, () => "none", async () =>
        {
            await Task.Yield();
            return false;
        }, default);
        await OperationLogging.RunAsync(_logger, Operation, () => "none", () => Task.CompletedTask, default);

        Assert.That(result, Is.False);
        Assert.That(_logs.Records.Select(record => record.Event.Id), Is.EqualTo(new[] { 1600, 1601, 1600, 1601 }));
        Assert.That(_logs.Records.Last().Message, Does.Contain("completed; no return value"));
    }

    [Test]
    public async Task Filtering_SkipsSummaryConstructionAndRetainsWarnings()
    {
        using var warningFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning).AddProvider(_logs));
        var logger = warningFactory.CreateLogger<OperationLoggingTests>();
        string UnexpectedSummary() => throw new AssertionException("Debug summary was evaluated while disabled.");
        OperationLogging.Run(logger, Operation, UnexpectedSummary, () => true);
        await OperationLogging.RunAsync(logger, Operation, UnexpectedSummary, () => Task.CompletedTask, default);
        var failure = new InvalidOperationException(Secret);
        Assert.Throws<InvalidOperationException>(() => OperationLogging.Run<bool>(logger, Operation, UnexpectedSummary, () => throw failure));
        Assert.That(_logs.Records.Select(record => record.Event.Id), Is.EqualTo(new[] { 1602 }));
        Assert.That(_logs.Records.Single().Level, Is.EqualTo(LogLevel.Warning));
        Assert.That(OperationLogging.WasReported(failure), Is.True);

        using var errorFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Error).AddProvider(_logs));
        var suppressed = new InvalidOperationException(Secret);
        OperationLogging.Failed(errorFactory.CreateLogger<OperationLoggingTests>(), Operation, suppressed, default);
        SarafanEvents.UnhandledException(errorFactory.CreateLogger<OperationLoggingTests>(), suppressed);
        Assert.That(OperationLogging.WasReported(suppressed), Is.False);
        Assert.That(_logs.Records, Has.Count.EqualTo(1));
        AssertPrivate();
    }

    [Test]
    public void Summaries_AllowOnlyExplicitValuesAndNeverInspectUnknownObjects()
    {
        var customer = new Customer { Phone = Secret, Profile = new CustomerProfile { FirstName = Secret } };
        var dto = CustomerDto.From(customer, true);
        var session = new AuthenticationSessionDto(Secret, DateTimeOffset.UtcNow, dto);
        var backofficeUser = new BackofficeUser
        {
            Email = Secret,
            NormalizedEmail = Secret,
            FirstName = Secret,
            LastName = Secret,
            PasswordHash = Secret,
            UserRoles = [new BackofficeUserRole { RoleCode = BackofficeRoles.Operator }]
        };
        var backofficeDto = BackofficeUserDto.From(backofficeUser);
        var backofficeIdentity = BackofficeIdentityDto.From(backofficeUser);
        var backofficeSession = new BackofficeAuthenticationSessionDto(
            Secret,
            DateTimeOffset.UtcNow,
            backofficeIdentity);
        var problem = new SarafanProblemDetailsFactory().Create(new DefaultHttpContext(), 400, "bad_request");
        problem.Detail = Secret;
        using var stream = new MemoryStream([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        object?[] values =
        [
            Secret, new PoisonValue(), null, customer, dto, session, new AuthenticationSession(session, Secret),
            backofficeUser, backofficeDto, backofficeIdentity, new[] { backofficeDto },
            new BackofficeRoleDto(Secret, Secret), new[] { new BackofficeRoleDto(Secret, Secret) },
            backofficeSession, new BackofficeAuthenticationSession(backofficeSession, Secret),
            new AccessTokenResult(Secret, DateTimeOffset.UtcNow), new CustomerProfileUpdateRequest { Address = Secret },
            new BackofficeLoginRequest { Email = Secret, Password = Secret },
            new BackofficeUserCreateRequest { Email = Secret, Password = Secret, Roles = [Secret] },
            new BackofficeUserUpdateRequest { Email = Secret, Password = Secret, Roles = [Secret] },
            new BackofficeSelfUpdateRequest { FirstName = Secret, LastName = Secret, Password = Secret },
            new RequestCodeRequest { Phone = Secret, Purpose = " REGISTER " },
            new RequestCodeRequest { Phone = Secret, Purpose = Secret },
            new RequestCodeRequest { Phone = Secret, Purpose = null! },
            new VerifyCodeRequest { Phone = Secret, Code = Secret, Purpose = "login" },
            new FormFile(stream, 0, 3, Secret, Secret), new FileContentResult([1, 2, 3], $"application/{Secret}"),
            problem, new ObjectResult(problem), new NoContentResult(), new EmptyResult(),
            new ServiceStatus("Sarafan.Core", "ok", VersionInfo.AppVersion), new ServiceStatus(Secret, Secret, Secret),
            cancellation.Token, CancellationToken.None, true, false
        ];

        var summaries = values.Select(LogValueSummary.Describe).ToArray();
        Assert.That(string.Join(' ', summaries), Does.Not.Contain(Secret));
        Assert.That(summaries, Does.Contain("[redacted]"));
        Assert.That(summaries, Does.Contain("null"));
        Assert.That(summaries, Does.Contain("status=204; no body"));
        Assert.That(summaries, Does.Contain("status=200; output=problem(status=400; details=[redacted])"));
        Assert.That(summaries, Does.Contain("cancellation requested=True"));
        Assert.That(summaries, Does.Contain("cancellation requested=False"));
        Assert.That(summaries, Does.Contain($"ServiceStatus(name=Sarafan.Core; status=ok; version={VersionInfo.AppVersion})"));
        Assert.That(summaries, Does.Contain("RequestCodeRequest(purpose=register; phone=[redacted])"));
        Assert.That(summaries, Does.Contain("BackofficeUserDto collection(count=1)"));
        Assert.That(summaries, Does.Contain("BackofficeRoleDto collection(count=1)"));
        Assert.That(summaries.Count(summary => summary.Contains("purpose=other")), Is.EqualTo(2));
        Assert.That(LogValueSummary.Inputs(), Is.EqualTo("none"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ControllerFilter_LogsNormalAndHandledExceptionResults(bool handledException)
    {
        var filter = new ControllerLoggingFilter(_factory.CreateLogger<ControllerLoggingFilter>());
        var context = ActionContext();
        Assert.That(filter.Order, Is.EqualTo(int.MinValue));
        await filter.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, [], context.Controller)
        {
            Result = new NoContentResult(),
            Exception = handledException ? new InvalidOperationException(Secret) : null,
            ExceptionHandled = handledException
        }));
        Assert.That(_logs.Records.Select(record => record.Event.Id), Is.EqualTo(new[] { 1600, 1601 }));
        Assert.That(_logs.Records.Last().Message, Does.Contain("status=204"));
        AssertPrivate();
    }

    [Test]
    public async Task ControllerFilter_LogsCoreStatusAtTrace()
    {
        using var traceFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Trace).AddProvider(_logs));
        var filter = new ControllerLoggingFilter(traceFactory.CreateLogger<ControllerLoggingFilter>());
        var context = ActionContext(status: true);

        await filter.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(
            context, [], context.Controller)
        {
            Result = new OkObjectResult(new ServiceStatus("Sarafan.Core", "ok", VersionInfo.AppVersion))
        }));

        Assert.That(_logs.Records.Select(record => record.Event.Id), Is.EqualTo(new[] { 1600, 1601 }));
        Assert.That(_logs.Records.Select(record => record.Event.Name), Is.EqualTo(new[]
        {
            SarafanEvents.OperationEnteredName, SarafanEvents.OperationExitedName
        }));
        Assert.That(_logs.Records.Select(record => record.Level), Is.All.EqualTo(LogLevel.Trace));
        Assert.That(_logs.Records.Select(record => record.Attributes["code.function.name"]),
            Is.All.EqualTo($"{typeof(StatusController).FullName}.{nameof(StatusController.Status)}"));
        AssertPrivate();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ControllerFilter_LogsReturnedAndThrownExceptionsWithoutHandlingThem(bool thrown)
    {
        var failure = new InvalidOperationException(Secret);
        var filter = new ControllerLoggingFilter(_factory.CreateLogger<ControllerLoggingFilter>());
        var context = ActionContext();
        var executed = new ActionExecutedContext(context, [], context.Controller) { Exception = failure };
        if (thrown)
        {
            var actual = Assert.ThrowsAsync<InvalidOperationException>(() => filter.OnActionExecutionAsync(context, () => throw failure));
            Assert.That(actual, Is.SameAs(failure));
        }
        else
        {
            await filter.OnActionExecutionAsync(context, () => Task.FromResult(executed));
            Assert.That(executed.Exception, Is.SameAs(failure));
            Assert.That(executed.ExceptionHandled, Is.False);
        }

        AssertFailure(failure, true, "unexpected exception");
    }

    [Test]
    public void JwtService_LogsUnexpectedFailureWhenCalledOutsideHttp()
    {
        var failure = new InvalidOperationException(Secret);
        var service = new JwtTokenService(
            Options.Create(new AuthenticationOptions { SigningKey = new string('x', 40) }),
            new FailingTimeProvider(failure), _factory.CreateLogger<JwtTokenService>());

        var actual = Assert.Throws<InvalidOperationException>(() => service.CreateAccessToken(new Customer { Phone = Secret }));
        Assert.That(actual, Is.SameAs(failure));
        AssertFailure(failure, true, "unexpected exception");
        Assert.That(_logs.Records.Single(record => record.Event.Id == 1602).Message, Does.Contain("JwtTokenService.CreateAccessToken"));
    }

    [Test]
    public async Task ExceptionHandler_UsesWarningFallbackAndDoesNotRepeatBoundaryFailure()
    {
        var handler = new SarafanExceptionHandler(new SarafanProblemDetailsFactory(), _factory.CreateLogger<SarafanExceptionHandler>());
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var failure = new InvalidOperationException(Secret);
        await handler.TryHandleAsync(context, failure, default);
        var fallback = _logs.Records.Single(record => record.Event.Id == 1400);
        Assert.That(fallback.Level, Is.EqualTo(LogLevel.Warning));
        Assert.That(fallback.Attributes["error.type"], Is.EqualTo(typeof(InvalidOperationException).FullName));
        Assert.That(context.Response.StatusCode, Is.EqualTo(500));

        OperationLogging.Failed(_logger, Operation, failure, default);
        await handler.TryHandleAsync(context, failure, default);
        Assert.That(_logs.Records.Count(record => record.Event.Id == 1400), Is.EqualTo(1));
        AssertPrivate();
    }

    [Test]
    public async Task ExceptionHandler_StartedResponseLogsNeutralWarningWithoutClaimingProblem()
    {
        using var body = new MemoryStream();
        var response = new StartedResponseFeature { Body = body, StatusCode = StatusCodes.Status202Accepted };
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(response);
        var handler = new SarafanExceptionHandler(
            new SarafanProblemDetailsFactory(_factory.CreateLogger<SarafanProblemDetailsFactory>()),
            _factory.CreateLogger<SarafanExceptionHandler>());

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException(Secret), default);

        Assert.That(handled, Is.False);
        Assert.That(response.StatusCode, Is.EqualTo(StatusCodes.Status202Accepted));
        Assert.That(body.Length, Is.Zero);
        AssertNeutralFallback();
        Assert.That(_logs.Records.Where(record => record.Event.Name == SarafanEvents.ProblemEmittedName), Is.Empty);
        Assert.That(_logs.Records.Last().Message, Does.Contain("Outputs: false"));
        AssertPrivate();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExceptionHandler_LogsProblemOnlyAfterSuccessfulWrite(bool failWrite)
    {
        var writeFailure = new IOException(Secret);
        using var body = new ControlledResponseStream(failWrite ? writeFailure : null);
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        var handler = new SarafanExceptionHandler(
            new SarafanProblemDetailsFactory(_factory.CreateLogger<SarafanProblemDetailsFactory>()),
            _factory.CreateLogger<SarafanExceptionHandler>());

        var handling = handler.TryHandleAsync(context, new InvalidOperationException(Secret), default).AsTask();
        bool claimedProblemBeforeWrite;
        try
        {
            await body.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            claimedProblemBeforeWrite = _logs.Records.Any(record => record.Event.Name == SarafanEvents.ProblemEmittedName);
        }
        finally
        {
            body.ContinueWrite.TrySetResult();
        }

        if (failWrite)
        {
            var actual = Assert.ThrowsAsync<IOException>(async () => await handling);
            Assert.That(actual, Is.SameAs(writeFailure));
            Assert.That(_logs.Records.Where(record => record.Event.Name == SarafanEvents.ProblemEmittedName), Is.Empty);
            Assert.That(_logs.Records.Last().Message, Does.Contain("no result; unexpected exception"));
        }
        else
        {
            Assert.That(await handling, Is.True);
            Assert.That(body.Length, Is.GreaterThan(0));
            var emitted = _logs.Records.Single(record => record.Event.Name == SarafanEvents.ProblemEmittedName);
            Assert.That(emitted.Attributes["sarafan.problem.code"], Is.EqualTo("internal_error"));
            Assert.That(emitted.Attributes["http.response.status_code"], Is.EqualTo(500));
            Assert.That(_logs.Records.Last().Message, Does.Contain("Outputs: true"));
        }

        Assert.That(claimedProblemBeforeWrite, Is.False);
        AssertNeutralFallback();
        AssertPrivate();
    }

    [TestCase(401, "invalid_code", 401, "invalid_code")]
    [TestCase(404, Secret, 500, "internal_error")]
    public async Task ProblemWrite_LogsActualNormalizedStatusAndCode(int status, string code, int expectedStatus, string expectedCode)
    {
        using var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        var factory = new SarafanProblemDetailsFactory(_factory.CreateLogger<SarafanProblemDetailsFactory>());

        await factory.WriteAsync(context, status, code);

        var emitted = _logs.Records.Single(record => record.Event.Name == SarafanEvents.ProblemEmittedName);
        Assert.That(context.Response.StatusCode, Is.EqualTo(expectedStatus));
        Assert.That(body.Length, Is.GreaterThan(0));
        Assert.That(emitted.Attributes["http.response.status_code"], Is.EqualTo(expectedStatus));
        Assert.That(emitted.Attributes["sarafan.problem.code"], Is.EqualTo(expectedCode));
        Assert.That(emitted.Attributes["trace_id"], Does.Match("^[0-9a-f]{32}$"));
        AssertPrivate();
    }

    private void AssertNeutralFallback()
    {
        var fallback = _logs.Records.Single(record => record.Event.Name == SarafanEvents.UnhandledExceptionName);
        Assert.That(fallback.Level, Is.EqualTo(LogLevel.Warning));
        Assert.That(fallback.Message, Is.EqualTo("An unhandled exception reached the centralized exception handler."));
        Assert.That(fallback.Attributes["error.type"], Is.EqualTo(typeof(InvalidOperationException).FullName));
        Assert.That(fallback.Attributes, Does.Not.ContainKey("ProblemCode"));
    }

    [Test]
    public async Task HttpFlow_LogsEveryControllerActionAndServiceBoundaryWithSafeOutputs()
    {
        using var app = IntegrationTestEnvironment.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging
                .AddProvider(_logs)
                .AddFilter<LogCollector>(null, LogLevel.Debug)
                .AddFilter<LogCollector>(
                    typeof(ControllerLoggingFilter).FullName!, LogLevel.Trace)));
        using var client = app.CreateClient();
        await ConsentTestData.AcceptMandatoryCookies(client);
        var phone = $"+79994{Random.Shared.Next(100000, 999999)}";
        using var status = await client.GetAsync("/api/v1/status/status");
        using var invalid = await client.PostAsJsonAsync("/api/v1/auth/code/request", new { phone = "", purpose = "login" });
        using var request = await client.PostAsJsonAsync("/api/v1/auth/code/request", await ConsentTestData.Request(client, phone));
        var onboarding = (await request.Content.ReadFromJsonAsync<CodeRequestDto>())!.OnboardingToken;
        using var verify = await client.PostAsJsonAsync("/api/v1/auth/code/verify", new
        {
            phone,
            purpose = "register",
            code = phone[^4..],
            termsAccepted = true,
            onboardingToken = onboarding
        });
        verify.EnsureSuccessStatusCode();
        var session = (await verify.Content.ReadFromJsonAsync<AuthenticationSessionDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var get = await client.GetAsync("/api/v1/customers/me");
        using var update = await client.PutAsJsonAsync("/api/v1/customers/me", new CustomerProfileUpdateRequest { FirstName = Secret });
        using var photo = await client.GetAsync("/api/v1/customers/me/photo");
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent([]), "file", Secret);
        using var putPhoto = await client.PutAsync("/api/v1/customers/me/photo", multipart);
        using var deletePhoto = await client.DeleteAsync("/api/v1/customers/me/photo");
        using var refresh = await client.PostAsync("/api/v1/auth/refresh", null);
        using var logout = await client.PostAsync("/api/v1/auth/logout", null);

        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(request.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(photo.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(putPhoto.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(deletePhoto.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(refresh.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        Type[] controllers = [typeof(AuthController), typeof(CustomersController), typeof(StatusController)];
        var actions = controllers.SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        foreach (var action in actions)
        {
            AssertBoundary($"{action.DeclaringType!.FullName}.{action.Name}");
        }

        var statusOperation = $"{typeof(StatusController).FullName}.{nameof(StatusController.Status)}";
        Assert.That(_logs.Records
            .Where(record => record.Event.Id is 1600 or 1601)
            .Where(record => Equals(record.Attributes["code.function.name"], statusOperation))
            .Select(record => record.Level), Is.All.EqualTo(LogLevel.Trace));

        foreach (var type in new[] { typeof(AuthenticationService), typeof(JwtTokenService) })
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                AssertBoundary($"{type.FullName}.{method.Name}");
            }
        }

        Assert.That(_logs.Records.Where(record => record.Event.Id == 1601).Any(record => record.Message.Contains("status=400")), Is.True);
        Assert.That(_logs.Records.Where(record => record.Event.Id == 1601).Any(record => record.Message.Contains("AuthenticationSession(tokens/customer=[redacted])")), Is.True);
        Assert.That(_logs.Records.Where(record => record.Event.Id == 1601).Any(record => record.Message.Contains("CustomerDto([redacted])")), Is.True);
        Assert.That(_logs.Records.Where(record => record.Event.Id == 1602), Is.Empty);
        Assert.That(string.Join(' ', _logs.Records.Select(record => record.Message)), Does.Not.Contain(phone).And.Not.Contain(session.AccessToken));
        AssertPrivate();
    }

    [Test]
    public async Task BackofficeHttpFlow_LogsEveryControllerAndServiceBoundaryWithoutIdentityOrSecrets()
    {
        using var app = IntegrationTestEnvironment.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(_logs).AddFilter<LogCollector>(null, LogLevel.Debug)));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true
        });
        using var login = await client.PostAsJsonAsync("/api/v1/backoffice/auth/login", new BackofficeLoginRequest
        {
            Email = IntegrationTestEnvironment.BackofficeEmail,
            Password = IntegrationTestEnvironment.BackofficePassword
        });
        login.EnsureSuccessStatusCode();
        var administrator = (await login.Content.ReadFromJsonAsync<BackofficeAuthenticationSessionDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", administrator.AccessToken);

        using var me = await client.GetAsync("/api/v1/backoffice/auth/me");
        using var roles = await client.GetAsync("/api/v1/backoffice/roles");
        using var operations = await client.GetAsync("/api/v1/backoffice/users/ops");
        using var users = await client.GetAsync("/api/v1/backoffice/users");
        var email = $"{Guid.NewGuid():N}@sarafan.test";
        const string password = "Operation_log_13";
        using var create = await client.PostAsJsonAsync("/api/v1/backoffice/users", new BackofficeUserCreateRequest
        {
            Email = email,
            FirstName = "Logging",
            LastName = "Test",
            Password = password,
            Roles = [BackofficeRoles.Operator]
        });
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<BackofficeUserDto>())!;
        using var get = await client.GetAsync($"/api/v1/backoffice/users/{created.Id}");
        using var update = await client.PutAsJsonAsync(
            $"/api/v1/backoffice/users/{created.Id}",
            new BackofficeUserUpdateRequest
            {
                Email = email,
                FirstName = "Logging",
                LastName = "Updated",
                IsActive = true,
                Roles = [BackofficeRoles.Operator]
            });
        using var updateSelf = await client.PutAsJsonAsync(
            "/api/v1/backoffice/users/me",
            new BackofficeSelfUpdateRequest
            {
                FirstName = administrator.User.FirstName,
                LastName = administrator.User.LastName
            });
        using var disable = await client.DeleteAsync($"/api/v1/backoffice/users/{created.Id}");
        using var refresh = await client.PostAsync("/api/v1/backoffice/auth/refresh", null);
        using var logout = await client.PostAsync("/api/v1/backoffice/auth/logout", null);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<BackofficeBootstrapService>().ProvisionAsync(default);
        }

        Assert.That(new[] { me, roles, operations, users, get, update, updateSelf, refresh },
            Is.All.Matches<HttpResponseMessage>(response => response.IsSuccessStatusCode));
        Assert.That(disable.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        Type[] controllers =
        [
            typeof(BackofficeAuthController),
            typeof(BackofficeUsersController),
            typeof(BackofficeRolesController)
        ];
        foreach (var action in controllers.SelectMany(type =>
                     type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)))
        {
            AssertBoundary($"{action.DeclaringType!.FullName}.{action.Name}");
        }

        Type[] services =
        [
            typeof(BackofficeAuthenticationService),
            typeof(BackofficeUserService),
            typeof(BackofficeJwtTokenService),
            typeof(BCryptBackofficePasswordHasher),
            typeof(BackofficeBootstrapService)
        ];
        foreach (var type in services)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                AssertBoundary($"{type.FullName}.{method.Name}");
            }
        }

        var messages = string.Join(' ', _logs.Records.Select(record => record.Message));
        Assert.That(messages, Does.Not.Contain(email).And.Not.Contain(password).And.Not.Contain(administrator.AccessToken));
        Assert.That(_logs.Records.Where(record => record.Event.Id == 1602), Is.Empty);
        AssertPrivate();
    }

    [Test]
    public async Task HttpFailure_LogsWarningForServiceAndControllerAndPreservesProblemResponse()
    {
        using var app = IntegrationTestEnvironment.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(_logs).AddFilter<LogCollector>(null, LogLevel.Debug));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IVerificationCodeProvider>();
                services.AddSingleton<IVerificationCodeProvider>(new FailingCodeProvider());
            });
        });
        using var client = app.CreateClient();
        await ConsentTestData.AcceptMandatoryCookies(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/code/request");
        request.Headers.Add("traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        request.Content = JsonContent.Create(new { phone = "+79993332211", purpose = "login" });
        using var response = await client.SendAsync(request);
        var problem = (await response.Content.ReadFromJsonAsync<SarafanProblemDetails>())!;
        var warnings = _logs.Records.Where(record => record.Event.Id == 1602).ToArray();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(problem.Code, Is.EqualTo("internal_error"));
        Assert.That(warnings, Has.Length.EqualTo(2));
        Assert.That(warnings.Select(record => record.Level), Is.All.EqualTo(LogLevel.Warning));
        Assert.That(warnings.Select(record => record.TraceId), Is.All.EqualTo(problem.TraceId));
        Assert.That(warnings[0].Message, Does.Contain("AuthenticationService.RequestCodeAsync"));
        Assert.That(warnings[1].Message, Does.Contain("AuthController.RequestCode"));
        Assert.That(_logs.Records.Where(record => record.Event.Id == 1400), Is.Empty);
        AssertBoundary($"{typeof(SarafanExceptionHandler).FullName}.TryHandleAsync");
        AssertBoundary($"{typeof(SarafanProblemDetailsFactory).FullName}.WriteAsync");
        AssertPrivate();
    }

    private void AssertBoundary(string operation)
    {
        var records = _logs.Records.Where(record => record.Attributes.TryGetValue("code.function.name", out var name) && Equals(name, operation)).ToArray();
        Assert.That(records.Count(record => record.Event.Id == 1600), Is.GreaterThan(0), operation);
        Assert.That(records.Count(record => record.Event.Id == 1601), Is.EqualTo(records.Count(record => record.Event.Id == 1600)), operation);
    }

    private void AssertFailure(Exception failure, bool warning, string output)
    {
        var warnings = _logs.Records.Where(record => record.Event.Id == 1602).ToArray();
        Assert.That(warnings, Has.Length.EqualTo(warning ? 1 : 0));
        if (warning)
        {
            Assert.That(warnings[0].Event.Name, Is.EqualTo(SarafanEvents.OperationFailedName));
            Assert.That(warnings[0].Level, Is.EqualTo(LogLevel.Warning));
            Assert.That(warnings[0].Attributes["error.type"], Is.EqualTo(failure.GetType().FullName));
            Assert.That(warnings[0].Message, Does.StartWith("Unexpected exception in ").And.Contain(failure.GetType().FullName));
        }

        Assert.That(_logs.Records.Last().Level, Is.EqualTo(LogLevel.Debug));
        Assert.That(_logs.Records.Last().Message, Does.Contain($"no result; {output}"));
        AssertPrivate();
    }

    private void AssertPrivate()
    {
        Assert.That(string.Join(' ', _logs.Records.Select(record => record.Message)), Does.Not.Contain(Secret));
        Assert.That(string.Join(' ', _logs.Records.SelectMany(record => record.Attributes.Values)), Does.Not.Contain(Secret));
        Assert.That(_logs.Records.Select(record => record.Exception), Is.All.Null);
    }

    private static bool ThrowFromOperation(Exception failure) => throw failure;

    private static ActionExecutingContext ActionContext(bool status = false)
    {
        var descriptor = new ControllerActionDescriptor
        {
            ControllerTypeInfo = status ? typeof(StatusController).GetTypeInfo() : typeof(CustomersController).GetTypeInfo(),
            MethodInfo = status
                ? typeof(StatusController).GetMethod(nameof(StatusController.Status))!
                : typeof(CustomersController).GetMethod(nameof(CustomersController.Get))!
        };
        var context = new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor);
        return new ActionExecutingContext(context, [], new Dictionary<string, object?> { ["input"] = Secret }, new object());
    }

    private sealed class PoisonValue
    {
        public string Value => throw new AssertionException("Unknown properties must never be read.");
        public override string ToString() => throw new AssertionException("Unknown values must never be formatted.");
    }

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private sealed class ControlledResponseStream(Exception? failure) : MemoryStream
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await ContinueWrite.Task.WaitAsync(cancellationToken);
            if (failure is not null)
            {
                throw failure;
            }

            await base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class FailingTimeProvider(Exception failure) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw failure;
    }

    private sealed class FailingCodeProvider : IVerificationCodeProvider
    {
        public Task RequestCodeAsync(string phone, CancellationToken cancellationToken) => Task.FromException(new InvalidOperationException(Secret));
        public Task<bool> VerifyCodeAsync(string phone, string? code, CancellationToken cancellationToken) => Task.FromException<bool>(new InvalidOperationException(Secret));
    }

    private sealed class LogCollector : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        public ConcurrentQueue<CapturedLog> Records { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CollectorLogger(this);
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public void Dispose() { }

        private sealed class CollectorLogger(LogCollector owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                // The test collector observes application events before output formatting.
                if (eventId.Name?.StartsWith("sarafan.core.", StringComparison.Ordinal) != true)
                {
                    return;
                }

                var attributes = new Dictionary<string, object?>();
                owner._scopes.ForEachScope((scope, target) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> values)
                    {
                        foreach (var pair in values)
                        {
                            target[pair.Key] = pair.Value;
                        }
                    }
                }, attributes);
                if (state is IEnumerable<KeyValuePair<string, object?>> stateValues)
                {
                    foreach (var pair in stateValues)
                    {
                        attributes[pair.Key] = pair.Value;
                    }
                }

                owner.Records.Enqueue(new CapturedLog(logLevel, eventId, formatter(state, exception), exception,
                    attributes, Activity.Current?.TraceId.ToHexString(), Activity.Current?.SpanId.ToHexString()));
            }
        }
    }

    private sealed record CapturedLog(LogLevel Level, EventId Event, string Message, Exception? Exception,
        IReadOnlyDictionary<string, object?> Attributes, string? TraceId, string? SpanId);
}
