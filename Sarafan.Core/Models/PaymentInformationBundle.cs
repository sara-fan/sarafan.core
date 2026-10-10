// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public enum PaymentRecipientType { LegalEntity = 0, IndividualEntrepreneur = 1 }

public sealed record PaymentInformationFields(PaymentRecipientType? RecipientType, string? RecipientName,
    string? Inn, string? Kpp, string? SettlementAccount, string? BankName, string? Bik,
    string? CorrespondentAccount, string? PaymentLink);

public sealed class PaymentInformationBundle
{
    private PaymentInformationBundle() { }
    internal PaymentInformationBundle(PaymentInformationFields fields, int actorId, DateTimeOffset now,
        (string ContentType, byte[] Content)? qr = null)
    {
        SetFields(fields);
        CreatedBy = UpdatedBy = actorId;
        CreatedAt = UpdatedAt = Normalize(now);
        if (qr.HasValue) SetQr(qr.Value);
    }

    public long Id { get; private set; }
    public PaymentRecipientType? RecipientType { get; private set; }
    public string? RecipientName { get; private set; }
    public string? Inn { get; private set; }
    public string? Kpp { get; private set; }
    public string? SettlementAccount { get; private set; }
    public string? BankName { get; private set; }
    public string? Bik { get; private set; }
    public string? CorrespondentAccount { get; private set; }
    public string? PaymentLink { get; private set; }
    public byte[]? QrContent { get; private set; }
    public string? QrContentType { get; private set; }
    public string? QrSha256 { get; private set; }
    public bool Enabled { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int CreatedBy { get; private set; }
    public int UpdatedBy { get; private set; }
    public bool Published { get; private set; }
    public Guid Version { get; private set; } = Guid.NewGuid();

    internal PaymentInformationFields Fields() => new(RecipientType, RecipientName, Inn, Kpp,
        SettlementAccount, BankName, Bik, CorrespondentAccount, PaymentLink);

    internal void Update(PaymentInformationFields fields, int actorId, DateTimeOffset now,
        (string ContentType, byte[] Content)? qr)
    {
        if (Published) throw new InvalidOperationException("Published payment contents are immutable.");
        SetFields(fields);
        if (qr.HasValue) SetQr(qr.Value);
        Advance(actorId, now);
    }

    internal void SetEnabled(bool enabled, int actorId, DateTimeOffset now)
    {
        Enabled = enabled;
        if (enabled) Published = true;
        Advance(actorId, now);
    }

    internal PaymentInformationBundle Copy(int actorId, DateTimeOffset now)
        => new(Fields(), actorId, now, QrContent is null ? null : (QrContentType!, QrContent));

    private void SetFields(PaymentInformationFields value)
    {
        RecipientType = value.RecipientType; RecipientName = value.RecipientName;
        Inn = value.Inn; Kpp = value.Kpp; SettlementAccount = value.SettlementAccount;
        BankName = value.BankName; Bik = value.Bik; CorrespondentAccount = value.CorrespondentAccount;
        PaymentLink = value.PaymentLink;
    }

    private void SetQr((string ContentType, byte[] Content) qr)
    {
        QrContent = qr.Content.ToArray();
        QrContentType = qr.ContentType;
        QrSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(QrContent));
    }

    private void Advance(int actorId, DateTimeOffset now)
    {
        var time = Normalize(now);
        UpdatedAt = time > UpdatedAt ? time : UpdatedAt.AddMicroseconds(1);
        UpdatedBy = actorId;
        Version = Guid.NewGuid();
    }

    private static DateTimeOffset Normalize(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        return new(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}
