// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed class LegalDocumentService(AppDbContext database, TimeProvider clock, ILogger<LegalDocumentService> logger)
{
    public Task<CurrentDocumentDto> CurrentAsync(string kind, CancellationToken token) => Run(nameof(CurrentAsync), async () =>
    {
        ValidateKind(kind);
        var now = clock.GetUtcNow();
        var current = await CurrentEntity(database, kind, now, token);
        var next = await NextChange(database, kind, now, token);
        return new CurrentDocumentDto(current is null ? null : ToDto(current, now, currentId: current.Id), now, next);
    }, token, kind);

    public Task<LegalDocumentDto> ReadAsync(Guid id, bool administrator, CancellationToken token) => Run(nameof(ReadAsync), async () =>
    {
        var row = await ReadEntity(id, administrator, token);
        var now = clock.GetUtcNow();
        var current = await CurrentEntity(database, row.Kind, now, token);
        return ToDto(row, now, administrator, current?.Id);
    }, token, id);

    public Task<byte[]> DownloadAsync(Guid id, bool administrator, CancellationToken token) => Run(nameof(DownloadAsync), async () =>
        (await ReadEntity(id, administrator, token)).Source, token, id);

    public Task<LegalDocumentDto[]> ListAsync(string? kind, CancellationToken token) => Run(nameof(ListAsync), async () =>
    {
        if (kind is not null) ValidateKind(kind);
        var rows = await database.LegalDocuments.AsNoTracking().Where(x => kind == null || x.Kind == kind)
            .OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync(token);
        var now = clock.GetUtcNow();
        var currentIds = new Dictionary<string, Guid?>();
        foreach (var type in rows.Select(x => x.Kind).Distinct())
            currentIds[type] = (await CurrentEntity(database, type, now, token))?.Id;
        return rows.Select(x => ToDto(x, now, true, currentIds[x.Kind])).ToArray();
    }, token, kind);

    public Task<LegalDocumentDto> SaveAsync(Guid? id, LegalDocumentRequest request, int actor, CancellationToken token) => Run(nameof(SaveAsync),
        () => ConsentTransaction.Run(database, async () =>
        {
            ValidateKind(request.Kind);
            if (request.Locale != "ru" || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200
                || string.IsNullOrWhiteSpace(request.DisplayVersion) || request.DisplayVersion.Length > 64
                || request.CookieCategories is null || request.CookieCategories.Distinct().Count() != request.CookieCategories.Length
                || request.CookieCategories.Any(x => x is not ("analytics" or "marketing"))
                || request.Kind != ConsentKinds.Cookies && request.CookieCategories.Length != 0)
                throw new ServiceException(400, "invalid_legal_document");
            var rendered = ConsentDocumentRenderer.Render(request.Source, request.FileName);
            var document = id is null ? new LegalDocument { CreatedAt = clock.GetUtcNow(), CreatedBy = actor }
                : await database.LegalDocuments.SingleOrDefaultAsync(x => x.Id == id, token)
                    ?? throw new ServiceException(404, "legal_document_not_found");
            if (id is not null && (document.State != "draft" || document.DisposedAt is not null || document.Revision != request.Revision))
                throw new ServiceException(409, "consent_conflict");
            var displayVersion = request.DisplayVersion.Trim();
            if (await database.LegalDocuments.AnyAsync(x => x.Kind == request.Kind && x.Locale == request.Locale
                && x.DisplayVersion == displayVersion && x.Id != document.Id, token))
                throw new ServiceException(409, "consent_conflict");
            document.Kind = request.Kind;
            document.Locale = request.Locale;
            document.Title = request.Title.Trim();
            document.DisplayVersion = displayVersion;
            document.Source = request.Source.ToArray();
            document.Html = rendered.Html;
            document.SourceHash = rendered.SourceHash;
            document.ContentHash = rendered.ContentHash;
            document.RendererVersion = ConsentDocumentRenderer.Version;
            document.CookieCategories = request.CookieCategories.Order().ToArray();
            document.UpdatedAt = clock.GetUtcNow();
            if (id is null) database.LegalDocuments.Add(document);
            else document.Revision++;
            Audit(document.Id, actor, id is null ? "draft-created" : "draft-updated");
            return ToDto(document, clock.GetUtcNow(), true);
        }, token), token, request);

    public Task<LegalDocumentDto> PublishAsync(Guid id, PublishDocumentRequest request, int actor, CancellationToken token) => Run(nameof(PublishAsync),
        () => ConsentTransaction.Run(database, async () =>
        {
            var document = await MutableDocument(id, request.Revision, "draft", token);
            var now = clock.GetUtcNow();
            if (request.Now == request.EffectiveDate.HasValue) throw new ServiceException(400, "invalid_effective_date");
            var effectiveAt = request.Now ? now : ConsentCalendar.Midnight(request.EffectiveDate!.Value);
            if (!request.Now && effectiveAt <= now) throw new ServiceException(400, "invalid_effective_date");
            if (await database.LegalDocuments.AnyAsync(x => x.Kind == document.Kind && x.Locale == document.Locale
                && x.State == "published" && (x.EffectiveAt > now || x.EffectiveAt == effectiveAt), token)) throw new ServiceException(409, "consent_conflict");
            document.State = "published";
            document.PublishedAt = now;
            document.EffectiveAt = effectiveAt;
            document.UpdatedAt = now;
            document.Revision++;
            Audit(id, actor, request.Now ? "published" : "scheduled");
            return ToDto(document, now, true, request.Now ? document.Id : null);
        }, token), token, request);

    public Task<LegalDocumentDto> CancelAsync(Guid id, int revision, int actor, CancellationToken token) => Run(nameof(CancelAsync),
        () => ConsentTransaction.Run(database, async () =>
        {
            var document = await MutableDocument(id, revision, "published", token);
            if (document.EffectiveAt <= clock.GetUtcNow()) throw new ServiceException(409, "consent_conflict");
            document.State = "cancelled";
            document.Revision++;
            document.UpdatedAt = clock.GetUtcNow();
            Audit(id, actor, "schedule-cancelled");
            return ToDto(document, clock.GetUtcNow(), true);
        }, token), token, id);

    public Task<LegalAuditDto[]> AuditAsync(Guid id, CancellationToken token) => Run(nameof(AuditAsync), () =>
        database.LegalAuditEvents.AsNoTracking().Where(x => x.DocumentId == id).OrderByDescending(x => x.Id)
            .Select(x => new LegalAuditDto(x.Id, x.ActorId, x.Action, x.At)).Take(200).ToArrayAsync(token), token, id);

    private Task<T> Run<T>(string method, Func<Task<T>> action, CancellationToken token, object? input)
        => OperationLogging.RunAsync(logger, $"{typeof(LegalDocumentService).FullName}.{method}",
            () => LogValueSummary.Inputs(("request", input)), action, token);

    private async Task<LegalDocument> ReadEntity(Guid id, bool administrator, CancellationToken token)
    {
        var row = await database.LegalDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (row is null || !administrator && (row.State != "published" || row.EffectiveAt > clock.GetUtcNow()))
            throw new ServiceException(404, "legal_document_not_found");
        if (row.DisposedAt is not null) throw new ServiceException(410, "legal_document_disposed");
        return row;
    }

    private async Task<LegalDocument> MutableDocument(Guid id, int revision, string state, CancellationToken token)
    {
        var row = await database.LegalDocuments.SingleOrDefaultAsync(x => x.Id == id, token)
            ?? throw new ServiceException(404, "legal_document_not_found");
        if (row.State != state || row.Revision != revision || row.DisposedAt is not null)
            throw new ServiceException(409, "consent_conflict");
        return row;
    }

    private void Audit(Guid document, int actor, string action) => database.LegalAuditEvents.Add(new LegalAuditEvent
    { DocumentId = document, ActorId = actor, Action = action, At = clock.GetUtcNow() });
    private static void ValidateKind(string kind)
    {
        if (!ConsentKinds.All.Contains(kind)) throw new ServiceException(400, "invalid_legal_document");
    }

    internal static Task<LegalDocument?> CurrentEntity(AppDbContext db, string kind, DateTimeOffset now, CancellationToken token) =>
        db.LegalDocuments.AsNoTracking().Where(x => x.Kind == kind && x.Locale == "ru" && x.State == "published" && x.EffectiveAt <= now)
            .OrderByDescending(x => x.EffectiveAt).ThenByDescending(x => x.PublishedAt).FirstOrDefaultAsync(token);
    internal static Task<DateTimeOffset?> NextChange(AppDbContext db, string kind, DateTimeOffset now, CancellationToken token) =>
        db.LegalDocuments.Where(x => x.Kind == kind && x.State == "published" && x.EffectiveAt > now)
            .Select(x => x.EffectiveAt).MinAsync(token);
    internal static LegalDocumentDto ToDto(LegalDocument row, DateTimeOffset now, bool admin = false, Guid? currentId = null) => new(
        row.Id, row.Kind, row.Locale, row.Title, row.DisplayVersion, row.Html, row.SourceHash, row.ContentHash,
        row.RendererVersion, row.CookieCategories, row.DisposedAt is not null ? "disposed" : row.State == "published"
            ? row.EffectiveAt > now ? "scheduled" : row.Id == currentId ? "effective" : "superseded" : row.State,
        row.EffectiveAt, row.Revision, row.CreatedAt, admin ? row.CreatedBy : null,
        row.EffectiveAt is { } effectiveAt ? ConsentCalendar.LocalDate(effectiveAt) : null, ConsentCalendar.TimeZoneId);
}
