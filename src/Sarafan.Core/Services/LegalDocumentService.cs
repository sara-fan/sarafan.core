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
    private const string CreatedAction = "created";
    private const string DeletedAction = "deleted";

    public Task<CurrentDocumentDto> CurrentAsync(LegalDocumentKind kind, CancellationToken token) => Run(nameof(CurrentAsync), async () =>
    {
        ValidateKind(kind);
        var now = clock.GetUtcNow();
        var current = await CurrentEntity(database, kind, now, token);
        var next = await NextChange(database, kind, now, token);
        return new CurrentDocumentDto(current is null ? null : ToDto(current, now), now, next);
    }, token, kind);

    public Task<LegalDocumentDto> ReadAsync(Guid id, bool administrator, CancellationToken token) => Run(nameof(ReadAsync), async () =>
    {
        var row = await ReadEntity(id, administrator, token);
        return ToDto(row, clock.GetUtcNow(), administrator);
    }, token, id);

    public Task<byte[]> DownloadAsync(Guid id, bool administrator, CancellationToken token) => Run(nameof(DownloadAsync), async () =>
        (await ReadEntity(id, administrator, token)).Source, token, id);

    public Task<LegalDocumentDto[]> ListAsync(LegalDocumentKind? kind, CancellationToken token) => Run(nameof(ListAsync), async () =>
    {
        if (kind is { } value) ValidateKind(value);
        var rows = await database.LegalDocuments.AsNoTracking().Where(x => kind == null || x.Kind == kind)
            .OrderByDescending(x => x.EffectiveAt).ThenByDescending(x => x.CreatedAt).Take(200).ToArrayAsync(token);
        var now = clock.GetUtcNow();
        return rows.Select(x => ToDto(x, now, true)).ToArray();
    }, token, kind);

    public Task<LegalDocumentPreviewDto> PreviewAsync(LegalDocumentPreviewRequest request, CancellationToken token) => Run(nameof(PreviewAsync), async () =>
    {
        var prepared = Prepare(request, clock.GetUtcNow(), false);
        await EnsureUnique(prepared.Kind, prepared.Locale, prepared.DisplayVersion, prepared.EffectiveAt, token);
        return new LegalDocumentPreviewDto(prepared.Html);
    }, token, request);

    public Task<LegalDocumentDto> CreateAsync(LegalDocumentRequest request, int actor, CancellationToken token) => Run(nameof(CreateAsync),
        () => ConsentTransaction.Run(database, async () =>
        {
            var now = clock.GetUtcNow();
            var prepared = Prepare(request, now, true);
            await EnsureUnique(prepared.Kind, prepared.Locale, prepared.DisplayVersion, prepared.EffectiveAt, token);
            var document = new LegalDocument
            {
                Kind = prepared.Kind,
                Locale = prepared.Locale,
                Title = prepared.Title,
                DisplayVersion = prepared.DisplayVersion,
                Source = request.Source.ToArray(),
                Html = prepared.Html,
                SourceHash = prepared.SourceHash,
                ContentHash = prepared.ContentHash,
                RendererVersion = prepared.RendererVersion,
                CookieCategories = prepared.CookieCategories,
                CreatedBy = actor,
                CreatedAt = now,
                EffectiveAt = prepared.EffectiveAt
            };
            database.LegalDocuments.Add(document);
            database.LegalDocumentAuditEvents.Add(Audit(document, actor, CreatedAction, now));
            return ToDto(document, now, true);
        }, token), token, request);

    public Task<bool> DeleteAsync(Guid id, int actor, CancellationToken token) => Run(nameof(DeleteAsync),
        () => ConsentTransaction.Run(database, async () =>
        {
            var document = await database.LegalDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token)
                ?? throw new ServiceException(404, "legal_document_not_found");
            var now = clock.GetUtcNow();
            var deleted = await database.LegalDocuments
                .Where(x => x.Id == id && x.EffectiveAt > now)
                .ExecuteDeleteAsync(token);
            if (deleted == 0)
                throw new ServiceException(409, "legal_document_already_effective");
            database.LegalDocumentAuditEvents.Add(Audit(document, actor, DeletedAction, now));
            return true;
        }, token), token, id);

    public Task<LegalDocumentAuditPageDto> AuditAsync(LegalDocumentKind? kind, string? action, string? search, Guid? documentId,
        int page, int pageSize, CancellationToken token) => Run(nameof(AuditAsync), async () =>
    {
        if (kind is { } value) ValidateKind(value);
        if (action is not null && action is not (CreatedAction or DeletedAction))
            throw new ServiceException(400, "invalid_legal_document_audit_filter");
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (search?.Length > 200) throw new ServiceException(400, "invalid_legal_document_audit_filter");
        var searchedId = Guid.TryParse(search, out var parsedDocumentId) ? parsedDocumentId : (Guid?)null;
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = database.LegalDocumentAuditEvents.AsNoTracking().Where(x =>
            (kind == null || x.Kind == kind) && (action == null || x.Action == action)
            && (documentId == null || x.DocumentId == documentId)
            && (search == null || EF.Functions.ILike(x.Title, $"%{search}%")
                || EF.Functions.ILike(x.DisplayVersion, $"%{search}%")
                || searchedId != null && x.DocumentId == searchedId));
        var total = await query.CountAsync(token);
        var rows = await query.OrderByDescending(x => x.At).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(token);
        var actorIds = rows.Select(x => x.ActorId).Distinct().ToArray();
        var actors = await database.BackofficeUsers.AsNoTracking().Where(x => actorIds.Contains(x.Id))
            .Select(x => new { x.Id, x.FirstName, x.LastName, x.Patronymic }).ToDictionaryAsync(x => x.Id, token);
        var items = rows.Select(x => new LegalDocumentAuditDto(x.Id, x.DocumentId, x.ActorId,
            actors.TryGetValue(x.ActorId, out var actor)
                ? string.Join(' ', new[] { actor.LastName, actor.FirstName, actor.Patronymic }.Where(value => !string.IsNullOrWhiteSpace(value)))
                : $"ID {x.ActorId}",
            x.Action, x.At, x.Kind, x.Locale, x.Title, x.DisplayVersion, x.EffectiveAt,
            ConsentCalendar.LocalDate(x.EffectiveAt), ConsentCalendar.TimeZoneId, x.SourceHash, x.ContentHash)).ToArray();
        return new LegalDocumentAuditPageDto(items, page, pageSize, total);
    }, token, new { kind, action, search, documentId, page, pageSize });

    private Task<T> Run<T>(string method, Func<Task<T>> action, CancellationToken token, object? input)
        => OperationLogging.RunAsync(logger, $"{typeof(LegalDocumentService).FullName}.{method}",
            () => LogValueSummary.Inputs(("request", input)), action, token);

    private async Task<LegalDocument> ReadEntity(Guid id, bool administrator, CancellationToken token)
    {
        var row = await database.LegalDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (row is null || !administrator && row.EffectiveAt > clock.GetUtcNow())
            throw new ServiceException(404, "legal_document_not_found");
        return row;
    }

    private async Task EnsureUnique(LegalDocumentKind kind, string locale, string displayVersion, DateTimeOffset effectiveAt, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(displayVersion)
            && await database.LegalDocuments.AnyAsync(x => x.Kind == kind && x.Locale == locale
            && x.DisplayVersion == displayVersion, token))
            throw new ServiceException(409, "legal_document_version_conflict");
        if (await database.LegalDocuments.AnyAsync(x => x.Kind == kind && x.Locale == locale
            && x.EffectiveAt == effectiveAt, token))
            throw new ServiceException(409, "legal_document_effective_date_conflict");
    }

    private static PreparedDocument Prepare(LegalDocumentPreviewRequest request, DateTimeOffset now, bool requireDisplayVersion)
    {
        if (request.Kind is not { } kind) throw new ServiceException(400, "invalid_legal_document_kind");
        ValidateKind(kind);
        if (request.Locale != "ru") throw new ServiceException(400, "invalid_legal_document_locale");
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200)
            throw new ServiceException(400, "invalid_legal_document_title");
        var displayVersion = request.DisplayVersion?.Trim() ?? "";
        if (displayVersion.Length > 64 || requireDisplayVersion && displayVersion.Length == 0)
            throw new ServiceException(400, "invalid_legal_document_version");
        if (request.EffectiveDate == default || request.EffectiveDate < ConsentCalendar.LocalDate(now))
            throw new ServiceException(400, "invalid_effective_date");
        var rendered = ConsentDocumentRenderer.Render(request.Source, request.FileName);
        var cookieCategories = kind == LegalDocumentKind.CookieConsent
            ? Enum.GetValues<CookieCategory>().Where(category => category.IsRequired()).OrderBy(category => (int)category).ToArray()
            : [];
        return new PreparedDocument(kind, request.Locale, request.Title.Trim(), displayVersion,
            cookieCategories, ConsentCalendar.Midnight(request.EffectiveDate), rendered.Html,
            rendered.SourceHash, rendered.ContentHash, ConsentDocumentRenderer.Version);
    }

    private static LegalDocumentAuditEvent Audit(LegalDocument document, int actor, string action, DateTimeOffset at) => new()
    {
        DocumentId = document.Id,
        ActorId = actor,
        Action = action,
        At = at,
        Kind = document.Kind,
        Locale = document.Locale,
        Title = document.Title,
        DisplayVersion = document.DisplayVersion,
        EffectiveAt = document.EffectiveAt,
        SourceHash = document.SourceHash,
        ContentHash = document.ContentHash
    };

    private static void ValidateKind(LegalDocumentKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ServiceException(400, "invalid_legal_document_kind");
    }

    internal static Task<LegalDocument?> CurrentEntity(AppDbContext db, LegalDocumentKind kind, DateTimeOffset now, CancellationToken token) =>
        db.LegalDocuments.AsNoTracking().Where(x => x.Kind == kind && x.Locale == "ru" && x.EffectiveAt <= now)
            .OrderByDescending(x => x.EffectiveAt).FirstOrDefaultAsync(token);

    internal static Task<DateTimeOffset?> NextChange(AppDbContext db, LegalDocumentKind kind, DateTimeOffset now, CancellationToken token) =>
        db.LegalDocuments.Where(x => x.Kind == kind && x.Locale == "ru" && x.EffectiveAt > now)
            .Select(x => (DateTimeOffset?)x.EffectiveAt).MinAsync(token);

    internal static LegalDocumentDto ToDto(LegalDocument row, DateTimeOffset now, bool administrator = false) => new(
        row.Id, row.Kind, row.Locale, row.Title, row.DisplayVersion, row.Html, row.SourceHash, row.ContentHash,
        row.RendererVersion, row.CookieCategories, row.EffectiveAt, row.CreatedAt, administrator ? row.CreatedBy : null,
        ConsentCalendar.LocalDate(row.EffectiveAt), ConsentCalendar.TimeZoneId, administrator ? row.EffectiveAt > now : null);

    public static LegalDocumentOpsDto Operations() => new(
        Enum.GetValues<LegalDocumentKind>()
            .OrderBy(kind => (int)kind)
            .Select(kind => new LegalDocumentOpsItemDto((int)kind, kind.GetDisplayName(), kind.GetRouteAlias()))
            .ToArray(),
        Enum.GetValues<CookieCategory>()
            .OrderBy(category => (int)category)
            .Select(category => new CookieCategoryOpsItemDto((int)category, category.GetDisplayName(), category.IsRequired()))
            .ToArray());

    private sealed record PreparedDocument(LegalDocumentKind Kind, string Locale, string Title, string DisplayVersion,
        CookieCategory[] CookieCategories, DateTimeOffset EffectiveAt, string Html, string SourceHash,
        string ContentHash, string RendererVersion);
}
