// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed class LogibooksClientTests
{
    private static readonly LogibooksGoods[] Goods = [new("12345678-1", "Товар", 12.34m, 2, "https://shop.test/item", "+79990001234")];
    private static readonly LogibooksRemoteResult Success = new(1, 1, 1, 0, [new(1, "12345678-1", "Created", 11, 7, 16, [])]);
    private static readonly LogibooksRemoteResult Rejected = new(null, 1, 0, 1, [new(1, null, "Invalid", null, null, null, [new("InvalidField", "unitPrice", "Ошибка цены")])]);
    private static readonly string Payload = LogibooksMapping.Serialize(new LogibooksPayload(new("Sarafan-demo", 2), Goods));
    private static HttpResponseMessage Response(int status, string body, string media = "application/json")
        => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, media) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    [TestCase(201, true)]
    [TestCase(422, true)]
    [TestCase(400, true)]
    [TestCase(403, true)]
    [TestCase(404, true)]
    [TestCase(409, true)]
    [TestCase(415, true)]
    [TestCase(500, false)]
    [TestCase(502, false)]
    [TestCase(200, false)]
    public async Task RecognizesOnlyDocumentedDefinitiveResults(int status, bool definitive)
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            requests++;
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
            if (request.RequestUri!.AbsolutePath == "/api/auth/login")
            {
                Assert.That(await request.Content!.ReadAsStringAsync(), Is.EqualTo("{\"email\":\"Sarafan\",\"password\":\"private\"}"));
                return Response(200, "{\"token\":\"secret-jwt\"}");
            }
            Assert.That(request.Headers.Authorization!.Parameter, Is.EqualTo("secret-jwt"));
            Assert.That(await request.Content!.ReadAsStringAsync(), Is.EqualTo(Payload));
            return Response(status, status == 201 ? LogibooksMapping.Serialize(Success)
                : status == 422 ? LogibooksMapping.Serialize(new { msg = "Ошибка", errors = new { }, details = Rejected })
                : "{\"msg\":\"Ошибка\",\"errors\":{}}");
        }));
        var client = new LogibooksClient(http, Options(), NullLogger<LogibooksClient>.Instance);
        var result = await client.CreateAsync("https://adapter.test", Payload, () => throw new AssertionException("Unexpected retry"), default);
        Assert.That(result.Definitive, Is.EqualTo(definitive)); Assert.That(requests, Is.EqualTo(2));
        if (status == 201) Assert.That(result.Result, Is.EqualTo(Success).UsingPropertiesComparer());
        if (!definitive) Assert.That(result.FailureCode, Is.EqualTo("logibooks_unknown"));
    }

    private static LogibooksTestOptions Options() => new(new() { Identifier = "Sarafan", Password = "private" });

    [Test]
    public async Task RefreshesOnlyOnceAfterExplicit401AndPersistsPermissionBeforeSecondPost()
    {
        var posts = 0; var logins = 0; var recorded = false;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login"))
            { logins++; return Task.FromResult(Response(200, "{\"token\":\"jwt\"}")); }
            posts++;
            if (posts == 2) Assert.That(recorded, Is.True);
            return Task.FromResult(Response(401, "{\"msg\":\"Нет доступа\",\"errors\":{}}"));
        }));
        var client = new LogibooksClient(http, Options(), NullLogger<LogibooksClient>.Instance);
        var result = await client.CreateAsync("https://adapter.test", Payload, () => { recorded = true; return Task.CompletedTask; }, default);
        Assert.That(posts, Is.EqualTo(2)); Assert.That(logins, Is.EqualTo(2));
        Assert.That(result.Definitive, Is.True); Assert.That(result.FailureCode, Is.EqualTo("logibooks_auth_failed"));
    }

    [TestCase(401)]
    [TestCase(403)]
    public async Task AuthenticationFailureDoesNotSubmitRegister(int status)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Response(status, "{}")); }));
        var result = await new LogibooksClient(http, Options(), NullLogger<LogibooksClient>.Instance)
            .CreateAsync("https://adapter.test", Payload, () => Task.CompletedTask, default);
        Assert.That(calls, Is.EqualTo(1)); Assert.That(result.Definitive, Is.True);
    }

    [TestCase("malformed")]
    [TestCase("{}")]
    [TestCase("[]")]
    [TestCase("{\"token\":42}")]
    [TestCase("{\"token\":\" \"}")]
    public async Task MalformedLoginIsUncertainWithoutRegisterPost(string body)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(200, body))));
        var result = await new LogibooksClient(http, Options(), NullLogger<LogibooksClient>.Instance)
            .CreateAsync("https://adapter.test", Payload, () => Task.CompletedTask, default);
        Assert.That(result.Definitive, Is.False);
    }

    [TestCase(201, "{}", "application/json")]
    [TestCase(201, "invalid", "application/json")]
    [TestCase(401, "{}", "application/json")]
    [TestCase(422, "{\"msg\":\"Ошибка\",\"errors\":{}}", "application/json")]
    [TestCase(400, "{\"msg\":\" \",\"errors\":{}}", "application/json")]
    [TestCase(400, "{\"msg\":\"Ошибка\",\"errors\":[]}", "application/json")]
    [TestCase(400, "{\"msg\":\"Ошибка\",\"errors\":{}}", "text/html")]
    public async Task DoesNotReleaseReservationForMalformedResponses(int status, string body, string media)
    {
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/login") ? Response(200, "{\"token\":\"jwt\"}") : Response(status, body, media))));
        var result = await new LogibooksClient(http, Options(), NullLogger<LogibooksClient>.Instance)
            .CreateAsync("https://adapter.test", Payload, () => throw new AssertionException("Unsafe retry"), default);
        Assert.That(result.Definitive, Is.False);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task NetworkAndTimeoutNeverResend(bool timeout)
    {
        using var http = new HttpClient(new Handler((_, _) => timeout
            ? throw new OperationCanceledException() : throw new HttpRequestException()));
        var result = await new LogibooksClient(http, Options(), NullLogger<LogibooksClient>.Instance)
            .CreateAsync("https://adapter.test", Payload, () => Task.CompletedTask, default);
        Assert.That(result.Definitive, Is.False);
    }

    [Test]
    public async Task ResponseLimitRejectsOversizedBody()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(200, new string('x', 2000)))));
        var options = Options(); options.CurrentValue.ResponseBytes = 100;
        var result = await new LogibooksClient(http, options, NullLogger<LogibooksClient>.Instance)
            .CreateAsync("https://adapter.test", Payload, () => Task.CompletedTask, default);
        Assert.That(result.Definitive, Is.False);
    }

    [Test]
    public void ValidatesCompleteUniqueOrderedReceiptAndConfigurableStatuses()
    {
        Assert.That(LogibooksClient.Valid(Success, Goods, true), Is.True);
        Assert.That(LogibooksClient.Valid(Rejected, Goods, false), Is.True);
        foreach (var invalid in new[] {
            Success with { Rows = null! }, Success with { Rows = [] }, Success with { RegisterId = null },
            Success with { Processed = 0 }, Success with { Created = -1 }, Success with { Failed = -1 },
            Success with { Created = 0, Failed = 1 },
            Success with { Rows = [null!] }, Success with { Rows = [Success.Rows[0] with { RowNumber = 2 }] },
            Success with { Rows = [Success.Rows[0] with { PostingNumber = "other" }] },
            Success with { Rows = [Success.Rows[0] with { Issues = null! }] },
            Success with { Rows = [Success.Rows[0] with { Issues = [null!] }] },
            Success with { Rows = [Success.Rows[0] with { Issues = [new("", null, "text")] }] },
            Success with { Rows = [Success.Rows[0] with { Issues = [new("code", null, "")] }] },
            Success with { Rows = [Success.Rows[0] with { ParcelId = 0 }] },
            Success with { Rows = [Success.Rows[0] with { StatusId = null }] },
            Success with { Rows = [Success.Rows[0] with { CheckStatus = null }] },
            Success with { Rows = [Success.Rows[0] with { Outcome = "unknown" }] },
            Success with { Rows = [Success.Rows[0] with { Outcome = "Invalid" }] },
            Rejected with { Rows = [Rejected.Rows[0] with { Outcome = "NotCreated", StatusId = 1 }] },
            Rejected with { Rows = [Rejected.Rows[0] with { Outcome = "Invalid", CheckStatus = 1 }] } })
            Assert.That(LogibooksClient.Valid(invalid, Goods, invalid.RegisterId is not null), Is.False);
        Assert.That(LogibooksClient.Valid(Rejected with { RegisterId = 1 }, Goods, false), Is.False);
        Assert.That(LogibooksClient.Valid(Success with
        {
            Rows = [Success.Rows[0], Success.Rows[0] with { RowNumber = 2, PostingNumber = Goods[0].PostingNumber }],
            Processed = 2,
            Created = 2
        }, [Goods[0], Goods[0]], true), Is.False);
    }

    [Test]
    public void ValidatesOptionsAndPaidUnitPrices()
    {
        var validator = new LogibooksOptionsValidator();
        Assert.That(validator.Validate(null, new()).Succeeded, Is.True);
        foreach (var bad in new[] { new LogibooksOptions { DeadlineSeconds = 0 }, new() { DeadlineSeconds = 3601 },
            new() { OutboundBytes = 0 }, new() { ResponseBytes = 0 }, new() { Enabled = true },
            new() { Enabled = true, BaseUrl = "ftp://adapter.test", Identifier = "a", Password = "b" },
            new() { Enabled = true, BaseUrl = "https://a:b@adapter.test", Identifier = "a", Password = "b" },
            new() { Enabled = true, BaseUrl = "https://adapter.test/?q=a", Identifier = "a", Password = "b" },
            new() { Enabled = true, BaseUrl = "https://adapter.test/#a", Identifier = "a", Password = "b" } })
            Assert.That(validator.Validate(null, bad).Failed, Is.True);
        Assert.That(validator.Validate(null, new() { Enabled = true, BaseUrl = "https://adapter.test/", Identifier = "a", Password = "b" }).Succeeded, Is.True);
        foreach (var price in new decimal?[] { null, 0, -1, 0.001m, 100000000m })
            Assert.That(LogibooksMapping.HasPrice(price, Currency.Usd), Is.False);
        Assert.That(LogibooksMapping.HasPrice(1, Currency.Rub), Is.False);
        Assert.That(LogibooksMapping.HasPrice(1, Currency.Usd), Is.True);
        Assert.That(LogibooksMapping.Ineligible(OrderStatus.Paid, 1, Currency.Usd, false), Is.Null);
        Assert.That(LogibooksMapping.Ineligible(OrderStatus.Paid, null, null, false), Is.Not.Null);
        Assert.That(LogibooksMapping.Ineligible(OrderStatus.UnderReview, 1, Currency.Usd, false), Is.Not.Null);
        Assert.That(LogibooksMapping.Ineligible(OrderStatus.Paid, 1, Currency.Usd, true), Is.Not.Null);
    }

    [Test]
    public void SearchSqlAndReservationIndexesCanBeInspectedWithoutOpeningDatabase()
    {
        using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=unused.invalid;Database=unused;Username=unused;Password=unused").Options);
        var sql = AppDatabaseOperations.For(database).SearchLogibooksRows(database, Guid.NewGuid(), "%_literal")
            .OrderBy(x => x.RowNumber).Take(25).ToQueryString();
        Assert.That(sql, Does.Contain("strpos").And.Contain("jsonb_array_elements").And.Contain("LIMIT"));
        Assert.That(sql, Does.Contain($"'{Currency.Usd.GetSymbol()}'").And.Not.Contain("' USD'"));
        foreach (var field in new[] { "tnVed", "productLink", "lastName", "firstName", "patronymic", "phone", "email", "postalCode", "city", "address",
            "passportSeries", "passportNumber", "passportIssuedBy", "passportIssueDate", "inn", "sender", "senderPhone", "senderEmail", "senderAddress",
            "countryCode", "senderCountryCode", "tradingCountryCode" })
            Assert.That(sql, Does.Contain("->>'" + field + "'"));
        Assert.That(sql, Does.Contain("concat_ws").And.Contain("DD.MM.YYYY").And.Contain("nullif(fields.value, '')"));
        var metadataSql = database.Set<LogibooksRegister>().Select(LogibooksRegisterService.Projection).ToQueryString();
        Assert.That(metadataSql, Does.Contain("count").And.Contain("failure").And.Not.Contain("request"));
        var model = database.Model.FindEntityType(typeof(LogibooksRegisterRow))!;
        Assert.That(model.GetIndexes().Single(x => x.GetDatabaseName() == "ux_logibooks_blocking_order").IsUnique, Is.True);
        Assert.That(model.GetIndexes().Single(x => x.GetDatabaseName() == "ux_logibooks_blocking_order").GetFilter(), Is.EqualTo("blocking"));
        Assert.That(database.Model.FindEntityType(typeof(LogibooksRegister))!.FindProperty(nameof(LogibooksRegister.Version))!.IsConcurrencyToken, Is.True);
    }
}
