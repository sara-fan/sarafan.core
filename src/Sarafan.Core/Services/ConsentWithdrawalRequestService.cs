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

    public Task<CustomerConsentWithdrawalRequestDto[]> ListAsync(CancellationToken token)
        => Run(nameof(ListAsync), () => database.CustomerConsentWithdrawalRequests
            .AsNoTracking()
            .OrderBy(item => item.Processed)
            .ThenBy(item => item.RequestedAt)
            .Select(item => new CustomerConsentWithdrawalRequestDto(item.CustomerId, item.RequestedAt, item.Processed))
            .ToArrayAsync(token), token, null);

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
