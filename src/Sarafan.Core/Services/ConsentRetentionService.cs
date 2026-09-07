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
            var heldCustomers = await database.ConsentRightsCases.Where(x => x.State != "completed").Select(x => x.CustomerId).Distinct().ToArrayAsync(token);
            var current = await LegalDocumentService.CurrentEntity(database, ConsentKinds.PersonalData, now, token);
            var removed = 0;
            long afterId = 0;
            while (true)
            {
                var candidates = await database.ConsentEvents.Where(x => x.RetainUntil <= now && x.Id > afterId).OrderBy(x => x.Id).Take(1000).ToArrayAsync(token);
                if (candidates.Length == 0) break;
                afterId = candidates[^1].Id;
                foreach (var item in candidates)
                {
                    if (item.CustomerId is { } customer && heldCustomers.Contains(customer)
                        || await database.ConsentAssociations.AnyAsync(x => x.ConsentEventId == item.Id && heldCustomers.Contains(x.CustomerId), token)) continue;
                    var latest = await database.ConsentEvents.AsNoTracking().Where(x => x.SubjectKey == item.SubjectKey && x.Kind == item.Kind)
                        .OrderByDescending(x => x.Id).FirstAsync(token);
                    // An operational current grant remains necessary while it authorizes active processing.
                    // Never remove a later denial while any older evidence could become the latest grant.
                    if (item.Id == latest.Id && item.Kind == ConsentKinds.PersonalData
                        && (item.Decision == "grant" && item.DocumentId == current?.Id
                            && await database.Customers.AnyAsync(x => x.Id == item.CustomerId && x.State != CustomerState.Disabled, token)
                            || await database.ConsentEvents.AnyAsync(x => x.SubjectKey == item.SubjectKey && x.Id < item.Id && x.RetainUntil > now, token))) continue;
                    database.ConsentEvents.Remove(item);
                    removed++;
                }
                await database.SaveChangesAsync(token);
            }
            var documents = await database.LegalDocuments.Where(x => x.DisposedAt == null
                && (x.State == "draft" ? x.UpdatedAt < draftCutoff : x.UpdatedAt < cutoff)).ToArrayAsync(token);
            var disposed = 0;
            foreach (var document in documents)
            {
                var active = await LegalDocumentService.CurrentEntity(database, document.Kind, now, token);
                if (active?.Id == document.Id || document.EffectiveAt > now
                    || await database.ConsentEvents.AnyAsync(x => x.DocumentId == document.Id, token)
                    || await database.ConsentOnboarding.AnyAsync(x => x.PersonalDataDocumentId == document.Id || x.TermsDocumentId == document.Id, token)) continue;
                document.CreatedBy = 0;
                document.Source = [];
                document.Html = "";
                document.DisposedAt = now;
                document.Revision++;
                database.LegalAuditEvents.Add(new LegalAuditEvent { DocumentId = document.Id, Action = "artifact-disposed", At = now });
                disposed++;
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
            catch (Exception)
            {
                SarafanEvents.ConsentRetentionFailed(logger);
            }
            try { await Task.Delay(TimeSpan.FromHours(24), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
