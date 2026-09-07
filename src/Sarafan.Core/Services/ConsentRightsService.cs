// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed class ConsentRightsService(AppDbContext database, TimeProvider clock, IOptions<ConsentOptions> options,
    ILogger<ConsentRightsService> logger)
{
    public Task<RightsCaseDto> CreateAsync(int customer, RightsRequest request, CancellationToken token) => Run(nameof(CreateAsync),
        () => ConsentTransaction.Run(database, async () =>
        {
            if (request.Kind is not ("withdrawal" or "stop-processing") || request.IdempotencyKey == Guid.Empty)
                throw new ServiceException(400, "invalid_rights_request");
            if (!await database.Customers.AnyAsync(x => x.Id == customer, token)) throw new ServiceException(404, "customer_not_found");
            var retry = await database.ConsentRightsCases.AsNoTracking().SingleOrDefaultAsync(x => x.CustomerId == customer && x.IdempotencyKey == request.IdempotencyKey, token);
            if (retry is not null)
            {
                if (retry.Kind != request.Kind) throw new ServiceException(409, "consent_conflict");
                return ToDto(retry);
            }
            var now = clock.GetUtcNow();
            var last = await database.ConsentEvents.Where(x => x.CustomerId == customer && x.Kind == ConsentKinds.PersonalData)
                .OrderByDescending(x => x.Id).FirstOrDefaultAsync(token);
            if (last is not null)
                database.ConsentEvents.Add(new ConsentEvent
                {
                    CustomerId = customer,
                    SubjectKey = $"customer:{customer}",
                    DocumentId = last.DocumentId,
                    ContentHash = last.ContentHash,
                    Kind = ConsentKinds.PersonalData,
                    Decision = "withdraw",
                    Source = "rights-request",
                    IdempotencyKey = Guid.NewGuid(),
                    At = now,
                    RetainUntil = now.AddDays(options.Value.EvidenceDays)
                });
            var responsible = await database.BackofficeUsers.Where(x => x.IsActive && x.UserRoles.Any(r => r.RoleCode == BackofficeRoles.Administrator))
                .OrderBy(x => x.Id).Select(x => (int?)x.Id).FirstOrDefaultAsync(token);
            var item = new ConsentRightsCase
            {
                CustomerId = customer,
                IdempotencyKey = request.IdempotencyKey,
                Kind = request.Kind,
                ReceivedAt = now,
                DueAt = request.Kind == "withdrawal" ? now.AddDays(30) : ConsentCalendar.WorkingDeadline(now, 10, options.Value),
                ResponsibleStaffId = responsible
            };
            database.ConsentRightsCases.Add(item);
            return ToDto(item);
        }, token), token, request);

    public Task<RightsCaseDto[]> ListAsync(int? customer, CancellationToken token) => Run(nameof(ListAsync), async () =>
        (await database.ConsentRightsCases.AsNoTracking().Where(x => customer == null || x.CustomerId == customer)
            .OrderBy(x => x.CompletedAt != null).ThenBy(x => x.DueAt).Take(200).ToArrayAsync(token)).Select(ToDto).ToArray(), token, customer);

    public Task<RightsCaseDto> UpdateAsync(Guid id, RightsCaseUpdate request, int actor, CancellationToken token) => Run(nameof(UpdateAsync),
        () => ConsentTransaction.Run(database, async () =>
        {
            var item = await database.ConsentRightsCases.SingleOrDefaultAsync(x => x.Id == id, token)
                ?? throw new ServiceException(404, "rights_case_not_found");
            if (item.Revision != request.Revision || item.State == "completed") throw new ServiceException(409, "consent_conflict");
            if (request.State is not ("open" or "in-progress" or "completed")
                || request.State == "completed" && string.IsNullOrWhiteSpace(request.CompletionEvidence)
                || request.RetentionBasis is null || request.CompletionEvidence is null || request.ExtensionReason is null
                || request.RetentionBasis.Length > 2000 || request.CompletionEvidence.Length > 2000 || request.ExtensionReason.Length > 1000)
                throw new ServiceException(400, "invalid_rights_request");
            if (request.ResponsibleStaffId is not null && !await database.BackofficeUsers.AnyAsync(x => x.Id == request.ResponsibleStaffId
                && x.IsActive && x.UserRoles.Any(r => r.RoleCode == BackofficeRoles.Administrator), token))
                throw new ServiceException(400, "invalid_rights_request");
            if (request.Extend)
            {
                if (item.Kind != "stop-processing" || item.Extended || clock.GetUtcNow() >= item.DueAt || string.IsNullOrWhiteSpace(request.ExtensionReason))
                    throw new ServiceException(400, "invalid_rights_request");
                item.DueAt = ConsentCalendar.WorkingDeadline(item.DueAt, 5, options.Value);
                item.ExtensionReason = request.ExtensionReason.Trim();
                item.Extended = true;
            }
            item.State = request.State;
            item.ResponsibleStaffId = request.ResponsibleStaffId;
            item.RetentionBasis = request.RetentionBasis.Trim();
            item.CompletionEvidence = request.CompletionEvidence.Trim();
            item.CompletedAt = item.State == "completed" ? clock.GetUtcNow() : null;
            item.Revision++;
            database.LegalAuditEvents.Add(new LegalAuditEvent
            {
                RightsCaseId = item.Id,
                ActorId = actor,
                Action = request.Extend ? "rights-extended" : "rights-updated",
                At = clock.GetUtcNow()
            });
            return ToDto(item);
        }, token), token, request);

    private Task<T> Run<T>(string name, Func<Task<T>> action, CancellationToken token, object? input) => OperationLogging.RunAsync(logger,
        $"{typeof(ConsentRightsService).FullName}.{name}", () => LogValueSummary.Inputs(("request", input)), action, token);
    internal static RightsCaseDto ToDto(ConsentRightsCase row) => new(row.Id, row.CustomerId, row.Kind, row.State,
        row.ReceivedAt, row.DueAt, row.ResponsibleStaffId, row.RetentionBasis, row.CompletionEvidence, row.ExtensionReason,
        row.Extended, row.CompletedAt, row.Revision);
}
