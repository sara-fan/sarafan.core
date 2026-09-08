// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed class ConsentRetentionService(AppDbContext database, TimeProvider clock,
    ILogger<ConsentRetentionService> logger)
{
    public Task<ConsentRetentionDto> SweepAsync(CancellationToken token) => OperationLogging.RunAsync(logger,
        $"{typeof(ConsentRetentionService).FullName}.{nameof(SweepAsync)}", () => LogValueSummary.Inputs(),
        () => ConsentTransaction.Run(database, async () =>
        {
            var now = clock.GetUtcNow();
            var onboarding = await database.ConsentOnboarding.Where(x => x.ExpiresAt <= now).ExecuteDeleteAsync(token);
            var currentId = (await LegalDocumentService.CurrentEntity(database, LegalDocumentKind.PersonalDataConsent, now, token))?.Id;
            var activeIds = await database.LegalDocuments.Where(x => x.EffectiveAt <= now)
                .GroupBy(x => new { x.Kind, x.Locale })
                .Select(group => group.OrderByDescending(x => x.EffectiveAt).First().Id)
                .ToArrayAsync(token);
            await database.ConsentReplayTombstones.Where(x => !activeIds.Contains(x.DocumentId)).ExecuteDeleteAsync(token);
            var removed = 0;
            long afterId = 0;
            while (true)
            {
                var candidates = await database.ConsentEvents.Where(x => x.RetainUntil <= now && x.Id > afterId)
                    .OrderBy(x => x.Id).Select(x => x.Id).Take(1000).ToArrayAsync(token);
                if (candidates.Length == 0) break;
                afterId = candidates[^1];
                // Evaluate the latest decisions in SQL with bounded round trips per page.
                // Keep the lock across the sweep so publication/decisions cannot race disposal.
                var expired = await database.ConsentEvents.Where(item => candidates.Contains(item.Id)
                    && !(!database.ConsentEvents.Any(x => x.SubjectKey == item.SubjectKey && x.Kind == item.Kind && x.Id > item.Id)
                        && (item.Kind == LegalDocumentKind.PersonalDataConsent && item.Decision == "grant" && item.DocumentId == currentId
                            && database.Customers.Any(x => x.Id == item.CustomerId && x.State != CustomerState.Disabled)
                            || database.ConsentEvents.Any(x => x.SubjectKey == item.SubjectKey && x.Kind == item.Kind && x.Id < item.Id && x.RetainUntil > now))))
                    .Select(item => new { item.Id, item.SubjectKey, item.IdempotencyKey, item.Kind, item.DocumentId }).ToArrayAsync(token);
                if (expired.Length == 0) continue;
                database.ConsentReplayTombstones.AddRange(expired.Where(x => activeIds.Contains(x.DocumentId)).Select(x => new ConsentReplayTombstone
                { KeyHash = ConsentService.ReplayKey(x.SubjectKey, x.IdempotencyKey, x.Kind), DocumentId = x.DocumentId }));
                await database.SaveChangesAsync(token);
                var ids = expired.Select(x => x.Id).ToArray();
                removed += await database.ConsentEvents.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(token);
            }
            return new ConsentRetentionDto(onboarding, removed);
        }, token), token);
}

public sealed class ConsentRetentionWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ConsentRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ConsentRetentionService>().SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                SarafanEvents.ConsentRetentionFailed(logger, exception);
            }
            try { await Task.Delay(TimeSpan.FromHours(24), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
