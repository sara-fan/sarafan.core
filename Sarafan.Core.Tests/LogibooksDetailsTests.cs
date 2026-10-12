// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Authentication;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class OrderPricingTests
{
    [Test]
    public async Task RegisterDetailsFreezeNormalizeAndParticipateInPreviewAndReplay()
    {
        var request = await RegisterCommand();
        var input = new LogibooksRegisterInput("  Сделка 42  ", "  Клиент 7  ", "  Накладная 9  ", new(2026, 10, 12));
        var preview = await Registers().PreviewAsync(new(request.OperationId, request.FileName, request.Orders, input), Shift, default);
        Assert.That(preview.Fingerprint, Is.Not.EqualTo(request.Fingerprint));
        Assert.That(preview.Register, Is.EqualTo(new LogibooksMetadata(request.FileName, 2,
            DealNumber: "Сделка 42", CustomerOrderNumber: "Клиент 7", InvoiceNumber: "Накладная 9", InvoiceDate: new(2026, 10, 12))));
        request = request with { Register = input };
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().CreateAsync(request, actorId, Shift, default)))!.Code,
            Is.EqualTo("logibooks_preview_changed"));
        request = request with { Fingerprint = preview.Fingerprint };
        var result = await Registers().CreateAsync(request, actorId, Shift, default);
        var frozen = await Registers().DetailsAsync(result.Id, Shift, default);
        Assert.That(frozen, Is.EqualTo(preview.Register));
        logibooksOptions.CurrentValue.Enabled = false;
        Assert.That((await Registers().CreateAsync(request with { Register = input with { DealNumber = "Сделка 42" } }, actorId, Shift, default)).Id, Is.EqualTo(result.Id));
        foreach (var change in new[] { input with { DealNumber = "changed" }, input with { CustomerOrderNumber = null },
            input with { InvoiceNumber = "changed" }, input with { InvoiceDate = new(2026, 10, 13) } })
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().CreateAsync(request with { Register = change }, actorId, Shift, default)))!.Code,
                Is.EqualTo("logibooks_update_conflict"));
        Assert.That((await Registers().DetailsAsync(result.Id, Shift, default)), Is.EqualTo(frozen));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().DetailsAsync(Guid.NewGuid(), Shift, default)))!.StatusCode, Is.EqualTo(404));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().DetailsAsync(result.Id, [BackofficeRoles.Operator], default)))!.StatusCode, Is.EqualTo(403));
    }

    [Test]
    public async Task EmptyRegisterExtensionPreservesLegacyCanonicalBytesAndStoredReads()
    {
        var request = await RegisterCommand();
        var legacyCommand = LogibooksMapping.Serialize(new { request.OperationId, request.FileName, request.Orders, request.MappingVersion, request.Fingerprint });
        Assert.That(LogibooksMapping.Serialize(request), Is.EqualTo(legacyCommand));
        var legacyRegister = """{"fileName":"legacy","realWeightKg":2,"customsProcedureCode":40,"transportationTypeCode":1,"incotermsCode":2}""";
        Assert.That(LogibooksMapping.Serialize(new LogibooksMetadata("legacy", 2)), Is.EqualTo(legacyRegister));
        var empty = new LogibooksRegisterInput("  ", "", null);
        var preview = await Registers().PreviewAsync(new(request.OperationId, request.FileName, request.Orders, empty), Shift, default);
        Assert.That(preview.Fingerprint, Is.EqualTo(request.Fingerprint));
        var result = await Registers().CreateAsync(request with { Register = empty }, actorId, Shift, default);
        var saved = await db.Set<LogibooksRegister>().SingleAsync();
        Assert.That(saved.IntentHash, Is.EqualTo(LogibooksMapping.Hash(legacyCommand)));
        Assert.That((await Registers().CreateAsync(request, actorId, Shift, default)).Id, Is.EqualTo(result.Id));
        Assert.That(await Registers().DetailsAsync(result.Id, Shift, default), Is.EqualTo(new LogibooksMetadata(request.FileName, 2)));
        Assert.That(saved.Request, Does.Not.Contain("senderPhone").And.Not.Contain("dealNumber"));
    }

    [Test]
    public async Task ExpandedParcelSearchUsesEveryDisplayedSnapshotValue()
    {
        var request = await RegisterCommand();
        var result = await Registers().CreateAsync(request, actorId, Shift, default);
        var saved = await db.Set<LogibooksRegister>().SingleAsync();
        var payload = LogibooksMapping.Read<LogibooksPayload>(saved.Request);
        var goods = payload.Parcels[0] with
        {
            TnVed = "1000000000",
            LastName = "Фамилия",
            FirstName = "Имя",
            Patronymic = "Отчество",
            Email = "recipient@example.test",
            PostalCode = "101000",
            City = "Москва",
            Address = "Улица Мира",
            PassportSeries = "4500",
            PassportNumber = "123456",
            PassportIssuedBy = "Отдел Москвы",
            PassportIssueDate = new(2020, 2, 29),
            Inn = "123456789012",
            Sender = "Sarafan Demo",
            SenderPhone = "+79991112233",
            SenderEmail = "sender@example.test",
            SenderAddress = "Адрес отправителя"
        };
        saved.Request = LogibooksMapping.Serialize(payload with { Parcels = [goods] });
        await db.SaveChangesAsync();
        foreach (var term in new[] { goods.PostingNumber, goods.ProductName, goods.ProductLink, goods.TnVed, goods.LastName, goods.FirstName,
            goods.Patronymic, goods.Phone!, goods.Email, goods.PostalCode, goods.City, goods.Address, goods.Inn, goods.Sender,
            goods.SenderPhone, goods.SenderEmail, goods.SenderAddress, "643", "4500 123456 выдан Отдел Москвы 29.02.2020", "1,000" })
            Assert.That((await Registers().RowsAsync(result.Id, 1, 10, term, Shift, default)).Pagination.TotalCount, Is.EqualTo(1), term);
        foreach (var passport in new[] { goods with { PassportSeries = null, PassportNumber = null, PassportIssuedBy = null, PassportIssueDate = null },
            goods with { PassportIssueDate = null }, goods with { PassportIssuedBy = null } })
        {
            saved.Request = LogibooksMapping.Serialize(payload with { Parcels = [passport] });
            await db.SaveChangesAsync();
            var term = passport.PassportSeries is null ? "—" : passport.PassportIssueDate is null ? "123456 выдан Отдел Москвы" : "123456 выдан 29.02.2020";
            Assert.That((await Registers().RowsAsync(result.Id, 1, 10, term, Shift, default)).Pagination.TotalCount, Is.EqualTo(1));
        }
    }
}
