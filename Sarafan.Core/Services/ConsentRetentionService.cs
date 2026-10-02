// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public interface IConsentRetentionService
{
    Task<ConsentRetentionDto> SweepAsync(CancellationToken token);
}
public sealed class ConsentRetentionService(AppDbContext database, TimeProvider clock,
    ILogger<ConsentRetentionService> logger) : IConsentRetentionService
{
    public Task<ConsentRetentionDto> SweepAsync(CancellationToken token) => OperationLogging.RunAsync(logger,
        $"{typeof(ConsentRetentionService).FullName}.{nameof(SweepAsync)}", () => LogValueSummary.Inputs(),
        () => ConsentTransaction.Run(database, async () =>
        {
            var operations = AppDatabaseOperations.For(database);
            var now = clock.GetUtcNow();
            var onboarding = await operations.DeleteAsync(
                database,
                database.ConsentOnboarding.Where(x => x.ExpiresAt <= now),
                token);
            var activeIds = await database.LegalDocuments.Where(x => x.EffectiveAt <= now)
                .GroupBy(x => new { x.Kind, x.Locale })
                .Select(group => group.OrderByDescending(x => x.EffectiveAt).First().Id)
                .ToArrayAsync(token);
            await operations.DeleteAsync(
                database,
                database.ConsentReplayTombstones.Where(x => !activeIds.Contains(x.DocumentId)),
                token);
            // Versioned decisions are permanent evidence; only temporary receipts and obsolete replay markers expire.
            return new ConsentRetentionDto(onboarding, 0);
        }, token), token);
}
