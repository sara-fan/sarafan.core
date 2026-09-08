// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;

using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed class ConsentWithdrawalRequestService(
    AppDbContext database,
    TimeProvider clock,
    ILogger<ConsentWithdrawalRequestService> logger)
{
    public Task<CustomerConsentWithdrawalRequestDto> CreateAsync(int customerId, CancellationToken token)
        => Run(nameof(CreateAsync), () => ConsentTransaction.Run(database, async () =>
        {
            if (!await database.Customers.AnyAsync(item => item.Id == customerId, token))
                throw new ServiceException(404, "customer_not_found");

            var pending = await database.CustomerConsentWithdrawalRequests
                .AsNoTracking()
                .Where(item => item.CustomerId == customerId && !item.Processed)
                .Select(item => new CustomerConsentWithdrawalRequestDto(item.CustomerId, item.RequestedAt, item.Processed))
                .SingleOrDefaultAsync(token);
            if (pending is not null) return pending;

            var now = clock.GetUtcNow().ToUniversalTime();
            var requestedAt = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
            var previous = await database.CustomerConsentWithdrawalRequests
                .Where(item => item.CustomerId == customerId)
                .MaxAsync(item => (DateTimeOffset?)item.RequestedAt, token);
            if (previous.HasValue && previous.Value >= requestedAt) requestedAt = previous.Value.AddTicks(10);
            var item = new CustomerConsentWithdrawalRequest
            {
                CustomerId = customerId,
                RequestedAt = requestedAt,
                Processed = false
            };
            database.CustomerConsentWithdrawalRequests.Add(item);
            return ToDto(item);
        }, token), token, customerId);

    public Task<CustomerConsentWithdrawalRequestPageDto> ListAsync(
        int page,
        int pageSize,
        string sortBy,
        string sortOrder,
        string? search,
        bool? processed,
        CancellationToken token)
        => Run(nameof(ListAsync), async () =>
        {
            search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
            var sortByKey = sortBy?.Trim().ToLowerInvariant();
            var sortOrderKey = sortOrder?.Trim().ToLowerInvariant();
            if (page < 1 || pageSize is < 1 or > 100
                || search is { Length: > 10 } || search?.Any(character => !char.IsAsciiDigit(character)) == true
                || sortByKey is not ("processed" or "requestedat" or "customerid")
                || sortOrderKey is not ("asc" or "desc"))
                throw new ServiceException(400, "invalid_consent_withdrawal_request_filter");

            var query = database.CustomerConsentWithdrawalRequests.AsNoTracking()
                .Where(item => (processed == null || item.Processed == processed)
                    && (search == null || EF.Functions.Like(item.CustomerId.ToString(), $"%{search}%")));
            var total = await query.CountAsync(token);
            var descending = sortOrderKey == "desc";
            var ordered = (sortByKey, descending) switch
            {
                ("processed", false) => query.OrderBy(item => item.Processed)
                    .ThenByDescending(item => item.RequestedAt).ThenBy(item => item.CustomerId),
                ("processed", true) => query.OrderByDescending(item => item.Processed)
                    .ThenByDescending(item => item.RequestedAt).ThenBy(item => item.CustomerId),
                ("requestedat", false) => query.OrderBy(item => item.RequestedAt).ThenBy(item => item.CustomerId),
                ("requestedat", true) => query.OrderByDescending(item => item.RequestedAt).ThenByDescending(item => item.CustomerId),
                ("customerid", false) => query.OrderBy(item => item.CustomerId).ThenByDescending(item => item.RequestedAt),
                _ => query.OrderByDescending(item => item.CustomerId).ThenByDescending(item => item.RequestedAt)
            };
            var offset = (long)(page - 1) * pageSize;
            var items = offset > int.MaxValue
                ? []
                : await ordered.Skip((int)offset).Take(pageSize)
                    .Select(item => new CustomerConsentWithdrawalRequestDto(item.CustomerId, item.RequestedAt, item.Processed))
                    .ToArrayAsync(token);
            var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
            return new CustomerConsentWithdrawalRequestPageDto
            {
                Items = items,
                Pagination = new PaginationInfo
                {
                    CurrentPage = page,
                    PageSize = pageSize,
                    TotalCount = total,
                    TotalPages = totalPages,
                    HasNextPage = page < totalPages,
                    HasPreviousPage = page > 1
                },
                Sorting = new SortingInfo
                {
                    SortBy = sortByKey switch
                    {
                        "requestedat" => "requestedAt",
                        "customerid" => "customerId",
                        _ => "processed"
                    },
                    SortOrder = sortOrderKey
                },
                Search = search
            };
        }, token, new { page, pageSize, sortBy, sortOrder, search, processed });

    public Task<CustomerConsentWithdrawalRequestDto> ProcessAsync(
        ProcessConsentWithdrawalRequest request,
        CancellationToken token)
        => Run(nameof(ProcessAsync), () => ConsentTransaction.Run(database, async () =>
        {
            if (request.RequestedAt.Offset != TimeSpan.Zero || request.RequestedAt.Ticks % 10 != 0)
                throw new ServiceException(404, "consent_withdrawal_request_not_found");
            var updated = await database.CustomerConsentWithdrawalRequests
                .Where(candidate => candidate.CustomerId == request.CustomerId
                    && candidate.RequestedAt == request.RequestedAt)
                .ExecuteUpdateAsync(properties => properties.SetProperty(candidate => candidate.Processed, true), token);
            if (updated == 0) throw new ServiceException(404, "consent_withdrawal_request_not_found");
            return new CustomerConsentWithdrawalRequestDto(request.CustomerId, request.RequestedAt, true);
        }, token), token, request);

    private Task<T> Run<T>(string name, Func<Task<T>> action, CancellationToken token, object? input)
        => OperationLogging.RunAsync(logger,
            $"{typeof(ConsentWithdrawalRequestService).FullName}.{name}",
            () => LogValueSummary.Inputs(("request", input)), action, token);

    internal static CustomerConsentWithdrawalRequestDto ToDto(CustomerConsentWithdrawalRequest row)
        => new(row.CustomerId, row.RequestedAt, row.Processed);
}
