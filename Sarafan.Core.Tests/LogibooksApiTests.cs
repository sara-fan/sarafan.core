// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class PaymentInformationApiTests
{
    [TestCase(BackofficeRoles.Administrator)]
    [TestCase(BackofficeRoles.ShiftManager)]
    public async Task RegisterHttpContractAdmits202AndReadsPagedEvidenceWithStaffIdentity(string role)
    {
        const string root = "/api/v1/backoffice/logibooks-registers";
        var staff = await StaffToken(role);
        await CustomerToken();
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<LogibooksOptions>>().CurrentValue;
        configuration.Enabled = true; configuration.BaseUrl = "https://unused.invalid";
        try
        {
            var customer = await database.Customers.SingleAsync(); customer.AllocateOrderNumber("12345678");
            var now = DateTimeOffset.UtcNow;
            var order = new Order(customer.Id, 1, "https://shop.test/item", 2, null, Guid.NewGuid(), now);
            order.SetProduct(new("Товар", new(12.34m, Currency.Usd), 2, null, null, null));
            order.UpdatePricing(now, true); order.MarkPaid(now);
            database.Add(order); await database.SaveChangesAsync();
            using var opsResponse = await Send(HttpMethod.Get, root + "/ops", staff);
            var ops = (await opsResponse.Content.ReadFromJsonAsync<LogibooksOpsDto>())!;
            Assert.That(ops.MaximumOrders, Is.EqualTo(1000)); Assert.That(ops.CanManage, Is.True);
            Assert.That(ops.UnitPriceCurrency, Is.EqualTo(new CurrencyOpsItemDto((int)Currency.Usd,
                Currency.Usd.GetDisplayName(), Currency.Usd.GetRouteAlias(), Currency.Usd.GetSymbol())));
            var details = new LogibooksRegisterInput("  Сделка 42  ", "Заказ 7", "Накладная 9", new(2026, 10, 12));
            using var invalidDate = await Send(HttpMethod.Post, root + "/preview", staff, JsonContent.Create(new
            {
                operationId = Guid.NewGuid(),
                orders = new[] { new LogibooksOrderSelection("12345678-1", order.UpdatedAt) },
                register = new { invoiceDate = "2026-02-30" }
            }));
            Assert.That(invalidDate.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            using var previewResponse = await Send(HttpMethod.Post, root + "/preview", staff,
                JsonContent.Create(new LogibooksPreviewRequest(Guid.NewGuid(), null, [new("12345678-1", order.UpdatedAt)], details)));
            previewResponse.EnsureSuccessStatusCode();
            var preview = (await previewResponse.Content.ReadFromJsonAsync<LogibooksPreviewDto>())!;
            var command = new LogibooksCreateRequest(preview.OperationId, preview.FileName,
                [new("12345678-1", order.UpdatedAt)], preview.MappingVersion, preview.Fingerprint, details);
            using var accepted = await Send(HttpMethod.Post, root, staff, JsonContent.Create(command));
            Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            Assert.That(accepted.Headers.Location!.OriginalString, Does.EndWith(root + "/" + command.OperationId));
            Assert.That(accepted.Headers.CacheControl!.NoStore, Is.True);
            var admitted = (await accepted.Content.ReadFromJsonAsync<LogibooksOperationDto>())!;
            using var detailRead = await Send(HttpMethod.Get, root + "/" + admitted.Id + "/details", staff);
            Assert.That(detailRead.Headers.CacheControl!.NoStore, Is.True);
            Assert.That(await detailRead.Content.ReadFromJsonAsync<LogibooksMetadata>(), Is.EqualTo(preview.Register));
            using var list = await Send(HttpMethod.Get, root + "?search=Sarafan", staff); list.EnsureSuccessStatusCode();
            using var read = await Send(HttpMethod.Get, root + "/" + admitted.Id, staff); read.EnsureSuccessStatusCode();
            using var rows = await Send(HttpMethod.Get, root + "/" + admitted.Id + "/rows?search=" + Uri.EscapeDataString("12,34" + ops.UnitPriceCurrency.Symbol), staff);
            Assert.That((await rows.Content.ReadFromJsonAsync<PagedResult<LogibooksRowDto>>())!.Pagination.TotalCount, Is.EqualTo(1));
            var saved = await database.Set<LogibooksRegister>().SingleAsync();
            saved.AttemptId = Guid.NewGuid(); saved.State = LogibooksState.Unknown; saved.Phase = LogibooksPhase.Dispatching;
            await database.SaveChangesAsync();
            using var reconcile = await Send(HttpMethod.Post, root + "/" + admitted.Id + "/reconcile", staff,
                JsonContent.Create(new LogibooksReconcileRequest(saved.Version, saved.AttemptId.Value, null, true, "Проверено",
                    [new(1, false, null)])));
            reconcile.EnsureSuccessStatusCode();
            Assert.That((await reconcile.Content.ReadFromJsonAsync<LogibooksOperationDto>())!.State, Is.EqualTo("Rejected"));
        }
        finally { configuration.Enabled = false; }
    }

    [Test]
    public async Task RegisterEndpointsRejectCustomerAnonymousAndUnqualifiedStaff()
    {
        const string root = "/api/v1/backoffice/logibooks-registers";
        var customer = await CustomerToken();
        foreach (var token in new[] { null, customer, await StaffToken(BackofficeRoles.Operator), await StaffToken(BackofficeRoles.SeniorOperator) })
        {
            var expected = token is null || token == customer ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            foreach (var path in new[] { root, root + "/ops", root + "/" + Guid.NewGuid(), root + "/" + Guid.NewGuid() + "/rows", root + "/" + Guid.NewGuid() + "/details" })
            { using var response = await Send(HttpMethod.Get, path, token); Assert.That(response.StatusCode, Is.EqualTo(expected)); }
            foreach (var path in new[] { root, root + "/preview", root + "/" + Guid.NewGuid() + "/reconcile" })
            { using var response = await Send(HttpMethod.Post, path, token, JsonContent.Create(new { })); Assert.That(response.StatusCode, Is.EqualTo(expected)); }
        }
    }
}
