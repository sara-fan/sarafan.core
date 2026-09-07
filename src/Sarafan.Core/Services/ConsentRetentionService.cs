// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed class ConsentRetentionService(AppDbContext database, TimeProvider clock, IOptions<ConsentOptions> options,
    ILogger<ConsentRetentionService> logger)
{
    public Task<ConsentRetentionDto> SweepAsync(CancellationToken token) => OperationLogging.RunAsync(logger,
        $"{typeof(ConsentRetentionService).FullName}.{nameof(SweepAsync)}", () => LogValueSummary.Inputs(),
        () => ConsentTransaction.Run(database, async () =>
        {
            var now = clock.GetUtcNow();
            var cutoff = now.AddDays(-options.Value.EvidenceDays);
            var draftCutoff = now.AddDays(-options.Value.DraftDays);
            var onboarding = await database.ConsentOnboarding.Where(x => x.ExpiresAt <= now).ExecuteDeleteAsync(token);
            var heldCustomers = database.ConsentRightsCases.Where(x => x.State != "completed").Select(x => x.CustomerId);
            var currentId = (await LegalDocumentService.CurrentEntity(database, ConsentKinds.PersonalData, now, token))?.Id;
            var removed = 0;
            long afterId = 0;
            while (true)
            {
                var candidates = await database.ConsentEvents.Where(x => x.RetainUntil <= now && x.Id > afterId)
                    .OrderBy(x => x.Id).Select(x => x.Id).Take(1000).ToArrayAsync(token);
                if (candidates.Length == 0) break;
                afterId = candidates[^1];
                // Evaluate holds and latest decisions in the database, with two round trips per page.
                // Keep the lock across the sweep so publication/decisions cannot race disposal.
                removed += await database.ConsentEvents.Where(item => candidates.Contains(item.Id)
                    && !(item.CustomerId.HasValue && heldCustomers.Contains(item.CustomerId.Value))
                    && !database.ConsentAssociations.Any(x => x.ConsentEventId == item.Id && heldCustomers.Contains(x.CustomerId))
                    && !(item.Kind == ConsentKinds.PersonalData
                        && !database.ConsentEvents.Any(x => x.SubjectKey == item.SubjectKey && x.Kind == item.Kind && x.Id > item.Id)
                        && (item.Decision == "grant" && item.DocumentId == currentId
                            && database.Customers.Any(x => x.Id == item.CustomerId && x.State != CustomerState.Disabled)
                            || database.ConsentEvents.Any(x => x.SubjectKey == item.SubjectKey && x.Id < item.Id && x.RetainUntil > now))))
                    .ExecuteDeleteAsync(token);
            }
            var activeIds = await database.LegalDocuments.Where(x => x.State == "published" && x.EffectiveAt <= now)
                .GroupBy(x => new { x.Kind, x.Locale })
                .Select(group => group.OrderByDescending(x => x.EffectiveAt).ThenByDescending(x => x.PublishedAt).First().Id)
                .ToArrayAsync(token);
            var disposed = 0;
            while (true)
            {
                // Filter all reference holds in SQL and fetch IDs only; source content can be large.
                var documents = await database.LegalDocuments.Where(x => x.DisposedAt == null
                    && (x.State == "draft" ? x.UpdatedAt < draftCutoff : x.UpdatedAt < cutoff)
                    && !activeIds.Contains(x.Id) && (x.EffectiveAt == null || x.EffectiveAt <= now)
                    && !database.ConsentEvents.Any(e => e.DocumentId == x.Id)
                    && !database.ConsentOnboarding.Any(o => o.PersonalDataDocumentId == x.Id || o.TermsDocumentId == x.Id))
                    .OrderBy(x => x.Id).Select(x => x.Id).Take(1000).ToArrayAsync(token);
                if (documents.Length == 0) break;
                disposed += await database.LegalDocuments.Where(x => documents.Contains(x.Id)).ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.CreatedBy, 0)
                    .SetProperty(x => x.Source, Array.Empty<byte>())
                    .SetProperty(x => x.Html, "")
                    .SetProperty(x => x.DisposedAt, now)
                    .SetProperty(x => x.Revision, x => x.Revision + 1), token);
                database.LegalAuditEvents.AddRange(documents.Select(id => new LegalAuditEvent
                { DocumentId = id, Action = "artifact-disposed", At = now }));
                await database.SaveChangesAsync(token);
            }
            var cases = await database.ConsentRightsCases.Where(x => x.CompletedAt < cutoff).ExecuteDeleteAsync(token);
            var audit = await database.LegalAuditEvents.Where(x => x.At < cutoff).ExecuteDeleteAsync(token);
            return new ConsentRetentionDto(onboarding, removed, disposed, cases, audit);
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
