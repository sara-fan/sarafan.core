// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Models;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

internal static class PaymentInformationRules
{
    internal const int NameMaxLength = 200;
    internal const int LinkMaxLength = 2048;
    internal const int RequestMaxBytes = ImageUpload.MaxBytes + 64 * 1024;

    internal static PaymentBundleOpsDto Operations() => new(
        [new(0, "Юридическое лицо", "legal-entity"), new(1, "Индивидуальный предприниматель", "individual-entrepreneur")],
        [new("draft", "Черновик"), new("enabled", "Включён"), new("disabled", "Отключён")],
        new(NameMaxLength, LinkMaxLength, ImageUpload.MaxBytes, ImageUpload.ContentTypes,
            ImageContent.MaxDimension, ImageContent.MaxPixels, ImageContent.MaxMetadataBytes), true);

    internal static PaymentInformationFields Prepare(PaymentInformationWriteRequest request)
    {
        static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var fields = new PaymentInformationFields(request.RecipientType, Text(request.RecipientName), Text(request.Inn),
            Text(request.Kpp), Text(request.SettlementAccount), Text(request.BankName), Text(request.Bik),
            Text(request.CorrespondentAccount), Text(request.PaymentLink));
        RequireValid(fields, false, false);
        return fields;
    }

    internal static Dictionary<string, string[]> Errors(PaymentInformationFields fields, bool complete, bool hasQr)
    {
        var errors = new Dictionary<string, string[]>();
        void Invalid(string field, string message) => errors[field] = [message];
        void Text(string field, string? value, int maximum)
        {
            if (value is not null && (value.Length > maximum || value.Any(char.IsControl)))
                Invalid(field, $"Укажите текст длиной не более {maximum} символов без управляющих символов.");
            else if (complete && value is null) Invalid(field, "Заполните обязательное поле.");
        }
        void Digits(string field, string? value, params int[] lengths)
        {
            if (value is not null && (!lengths.Contains(value.Length) || value.Any(character => character is < '0' or > '9')))
                Invalid(field, "Проверьте количество цифр и формат реквизита.");
            else if (complete && value is null) Invalid(field, "Заполните обязательный реквизит.");
        }
        if (fields.RecipientType.HasValue && !Enum.IsDefined(fields.RecipientType.Value)
            || complete && fields.RecipientType is null) Invalid("recipientType", "Выберите тип получателя.");
        Text("recipientName", fields.RecipientName, NameMaxLength);
        Text("bankName", fields.BankName, NameMaxLength);
        Digits("inn", fields.Inn, fields.RecipientType is PaymentRecipientType.LegalEntity ? [10]
            : fields.RecipientType is PaymentRecipientType.IndividualEntrepreneur ? [12] : [10, 12]);
        if (fields.RecipientType is PaymentRecipientType.IndividualEntrepreneur)
        {
            if (fields.Kpp is not null) Invalid("kpp", "КПП для индивидуального предпринимателя не указывается.");
        }
        else if (fields.Kpp is not null || fields.RecipientType is PaymentRecipientType.LegalEntity)
            Digits("kpp", fields.Kpp, 9);
        Digits("settlementAccount", fields.SettlementAccount, 20);
        Digits("bik", fields.Bik, 9);
        Digits("correspondentAccount", fields.CorrespondentAccount, 20);
        if (fields.PaymentLink is not null && !ValidLink(fields.PaymentLink))
            Invalid("paymentLink", "Укажите полную банковскую ссылку HTTPS без встроенного логина и пароля.");
        else if (complete && fields.PaymentLink is null) Invalid("paymentLink", "Укажите банковскую платёжную ссылку.");
        if (complete && !hasQr) Invalid("qr", "Загрузите статический QR СБП получателя.");
        return errors;
    }

    internal static void RequireValid(PaymentInformationFields fields, bool complete, bool hasQr)
    {
        var errors = Errors(fields, complete, hasQr);
        if (errors.Count > 0) throw new ServiceException(400, "validation_failed") { Errors = errors };
    }

    internal static ServiceException InvalidQr(string code) => new(400, "validation_failed")
    {
        Errors = new Dictionary<string, string[]>
        {
            ["qr"] = [code switch
        {
            "size" => $"Размер файла должен быть от 1 до {ImageUpload.MaxBytes} байт.",
            "type" => "Выберите статический файл PNG, JPEG или WebP.",
            _ => "Файл изображения повреждён, содержит анимацию или превышает допустимые размеры."
        }]
        }
    };

    internal static Guid RequireVersion(Guid? version) => version is null || version == Guid.Empty
        ? throw new ServiceException(400, "invalid_payment_bundle_version") : version.Value;

    private static bool ValidLink(string value) => value.Length <= LinkMaxLength
        && !value.Any(char.IsControl) && !value.Contains('\\')
        && value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);
}
