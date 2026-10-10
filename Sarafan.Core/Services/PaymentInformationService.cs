// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed partial class PaymentInformationService(AppDbContext database, TimeProvider clock, ILogger<PaymentInformationService> logger)
{
    private static readonly Expression<Func<PaymentInformationBundle, PaymentBundleDto>> StaffProjection = row => new(
        row.Id, row.Version, new(row.RecipientType, row.RecipientName, row.Inn, row.Kpp, row.SettlementAccount,
            row.BankName, row.Bik, row.CorrespondentAccount, row.PaymentLink),
        row.Enabled ? "enabled" : !row.Published ? "draft" : "disabled", row.Enabled,
        row.QrSha256 == null ? null : "/api/v1/backoffice/payment-information-bundles/" + row.Id + "/qr?v=" + row.QrSha256,
        row.CreatedAt, row.UpdatedAt, row.CreatedBy, row.UpdatedBy,
        !row.Published, false, row.Enabled, !row.Enabled, row.Published);

    private static void RequireManage(string[] roles) => BackofficeAuthorization.RequireAllowed(roles, BackofficeAction.ManagePaymentInformation);
    public PaymentBundleOpsDto Operations(string[] roles)
        => OperationLogging.Run(logger, typeof(PaymentInformationService).FullName + "." + nameof(Operations),
            () => LogValueSummary.Inputs(("roles", roles)),
            () => { RequireManage(roles); return PaymentInformationRules.Operations(); });

    public Task<PaymentBundlePageDto> ListAsync(string[] roles, int page, int pageSize, string sortBy,
        string sortOrder, string? search, string? state, CancellationToken token)
        => Run(nameof(ListAsync), async () =>
        {
            RequireManage(roles);
            search = search?.Trim();
            if (page < 1 || page > 1000000 || pageSize is not (10 or 25 or 50 or 100)
                || sortOrder is not ("asc" or "desc")
                || sortBy is not ("id" or "recipientName" or "inn" or "bankName" or "state" or "createdAt")
                || state is not (null or "draft" or "enabled" or "disabled") || search?.Length > 200)
                throw new ServiceException(400, "invalid_payment_bundle_filter");
            var query = database.PaymentInformationBundles.AsNoTracking();
            query = state switch
            {
                "draft" => query.Where(row => !row.Published),
                "enabled" => query.Where(row => row.Enabled),
                "disabled" => query.Where(row => !row.Enabled && row.Published),
                _ => query
            };
            if (!string.IsNullOrEmpty(search)) query = ListDisplaySearch.PaymentBundles(query, search);
            var count = await query.CountAsync(token);
            var rows = await Sort(query, sortBy, sortOrder == "desc").Skip((page - 1) * pageSize)
                .Take(pageSize).Select(StaffProjection).ToArrayAsync(token);
            var pages = (int)Math.Ceiling((double)count / pageSize);
            return new PaymentBundlePageDto
            {
                Items = rows.Select(Capabilities).ToArray(),
                Search = search,
                State = state,
                Sorting = new() { SortBy = sortBy, SortOrder = sortOrder },
                Pagination = new()
                {
                    CurrentPage = page,
                    PageSize = pageSize,
                    TotalCount = count,
                    TotalPages = pages,
                    HasNextPage = page < pages,
                    HasPreviousPage = page > 1
                },
                EnabledBundle = await database.PaymentInformationBundles.AsNoTracking().Where(row => row.Enabled)
                    .Select(row => new EnabledPaymentBundle(row.Id, row.Version)).SingleOrDefaultAsync(token)
            };
        }, token);

    private static IOrderedQueryable<PaymentInformationBundle> Sort(IQueryable<PaymentInformationBundle> query, string key, bool descending)
    {
        Expression<Func<PaymentInformationBundle, object?>> expression = key switch
        {
            "recipientName" => row => row.RecipientName,
            "inn" => row => row.Inn,
            "bankName" => row => row.BankName,
            "state" => row => row.Enabled ? "Включён" : !row.Published ? "Черновик" : "Отключён",
            "createdAt" => row => row.CreatedAt,
            _ => row => row.Id
        };
        return (descending ? query.OrderByDescending(expression) : query.OrderBy(expression)).ThenByDescending(row => row.Id);
    }

    public Task<PaymentBundleDto> GetAsync(long id, string[] roles, CancellationToken token)
        => Run(nameof(GetAsync), async () =>
        {
            RequireManage(roles);
            var value = await database.PaymentInformationBundles.AsNoTracking().Where(row => row.Id == id)
                .Select(StaffProjection).SingleOrDefaultAsync(token) ?? throw new ServiceException(404, "resource_not_found");
            return Capabilities(value);
        }, token);

    private static PaymentBundleDto Capabilities(PaymentBundleDto value) => value with
    {
        CanEnable = !value.Enabled && PaymentInformationRules.Errors(value.Information, true, value.QrUrl is not null).Count == 0
    };

    public Task<CurrentPaymentInformationDto> CurrentAsync(CancellationToken token)
        => Run(nameof(CurrentAsync), async () => new CurrentPaymentInformationDto(
            await database.PaymentInformationBundles.AsNoTracking().Where(row => row.Enabled)
                .Select(row => new PublicPaymentInformationDto(row.Id, new(row.RecipientType, row.RecipientName,
                    row.Inn, row.Kpp, row.SettlementAccount, row.BankName, row.Bik, row.CorrespondentAccount, row.PaymentLink),
                    "/api/v1/payment-information/current/qr?v=" + row.QrSha256)).SingleOrDefaultAsync(token)), token);

    public Task<StoreLogoDto> QrAsync(long? id, string? digest, string[]? roles, CancellationToken token)
        => Run(nameof(QrAsync), async () =>
        {
            if (id.HasValue) RequireManage(roles ?? []);
            var image = await database.PaymentInformationBundles.AsNoTracking()
                .Where(row => id.HasValue ? row.Id == id.Value : row.Enabled)
                .Where(row => row.QrSha256 != null && row.QrSha256 == digest)
                .Select(row => new StoreLogoDto(row.QrContent!, row.QrContentType!, row.QrSha256!))
                .SingleOrDefaultAsync(token) ?? throw new ServiceException(404, "resource_not_found");
            if (image.ContentSha256 != Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image.Content)))
                throw new ServiceException(404, "resource_not_found");
            return image;
        }, token);

    private Task<T> Run<T>(string name, Func<Task<T>> action, CancellationToken token)
        => OperationLogging.RunAsync(logger, typeof(PaymentInformationService).FullName + "." + name,
            () => "paymentInformation=[redacted]", action, token);
}
