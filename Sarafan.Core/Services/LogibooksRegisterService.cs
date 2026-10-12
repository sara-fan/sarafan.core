// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Linq.Expressions;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed partial class LogibooksRegisterService(AppDbContext database, IOptionsMonitor<LogibooksOptions> options,
    TimeProvider clock, SarafanProblemDetailsFactory problems, ILogger<LogibooksRegisterService> logger)
{
    private Task<T> Run<T>(string method, Func<string> inputs, Func<Task<T>> action, CancellationToken token)
        => OperationLogging.RunAsync(logger, $"{typeof(LogibooksRegisterService).FullName}.{method}", inputs, action, token);
    private static void Authorize(string[] roles) => BackofficeAuthorization.RequireAllowed(roles, BackofficeAction.ManageLogibooksRegisters);
    private static ServiceException Invalid(string field, string message) => new(400, "invalid_logibooks_register")
    { Errors = new Dictionary<string, string[]> { [field] = [message] } };
    private string Failure(string code) => LogibooksMapping.Serialize(problems.Create(new DefaultHttpContext(), 502, code));
    private static readonly string[] StateNames = ["Отправляется", "Создан", "Создан частично", "Отклонён", "Результат неизвестен"];
    internal static string StateName(LogibooksState state) => StateNames[(int)state];

    public Task<LogibooksOpsDto> OperationsAsync(string[] roles, CancellationToken token)
        => Run(nameof(OperationsAsync), () => LogValueSummary.Inputs((nameof(roles), roles), (nameof(token), token)), () =>
        {
            Authorize(roles);
            var configuration = options.CurrentValue;
            return Task.FromResult(new LogibooksOpsDto(configuration.Enabled, true, LogibooksMapping.MaximumOrders,
                LogibooksMapping.MaximumLabel, LogibooksMapping.MaximumCommand, configuration.OutboundBytes,
                configuration.ResponseBytes, LogibooksMapping.Version, LogibooksMapping.DemoDefaults,
                Enum.GetValues<LogibooksState>().Select(x => new EnumOpsItemDto((int)x, StateName(x), x.ToString())).ToArray(),
                new CurrencyOpsItemDto((int)Currency.Usd, Currency.Usd.GetDisplayName(), Currency.Usd.GetRouteAlias(), Currency.Usd.GetSymbol())));
        }, token);

    public Task<LogibooksPreviewDto> PreviewAsync(LogibooksPreviewRequest request, string[] roles, CancellationToken token)
        => Run<LogibooksPreviewDto>(nameof(PreviewAsync), () => LogValueSummary.Inputs((nameof(request), request), (nameof(roles), roles), (nameof(token), token)), async () =>
        {
            Authorize(roles);
            if (!options.CurrentValue.Enabled) throw new ServiceException(503, "logibooks_disabled");
            var label = Normalize(request.OperationId, request.FileName ?? LogibooksMapping.Label(request.OperationId, clock.GetUtcNow()), request.Orders);
            var orders = await ResolveAsync(request.Orders, false, token);
            var payload = await MapAsync(orders, request.Orders, label, LogibooksMapping.NormalizeRegister(request.Register), token);
            return new(request.OperationId, label, LogibooksMapping.Version, LogibooksMapping.Hash(LogibooksMapping.Serialize(payload)),
                payload.Register, payload.Parcels, LogibooksMapping.DemoDefaults);
        }, token);

    public Task<LogibooksOperationDto> CreateAsync(LogibooksCreateRequest request, int actorId, string[] roles, CancellationToken token)
        => Run(nameof(CreateAsync), () => LogValueSummary.Inputs((nameof(request), request), (nameof(actorId), actorId),
            (nameof(roles), roles), (nameof(token), token)), async () =>
        {
            Authorize(roles);
            var label = Normalize(request.OperationId, request.FileName, request.Orders);
            var hash = LogibooksMapping.Hash(LogibooksMapping.Serialize(request with { FileName = label, Register = LogibooksMapping.NormalizeRegister(request.Register), Orders = request.Orders.Select(x => x with { ExpectedUpdatedAt = x.ExpectedUpdatedAt.ToUniversalTime() }).ToArray() }));
            var operations = AppDatabaseOperations.For(database);
            await using var transaction = await operations.BeginTransactionAsync(database, token);
            await operations.LockLogibooksAsync(database, token);
            var previous = await database.Set<LogibooksRegister>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.OperationId, token);
            if (previous is not null)
            {
                if (previous.IntentHash != hash) throw new ServiceException(409, "logibooks_update_conflict");
                await transaction.CommitAsync(token);
                return await GetCoreAsync(previous.Id, token);
            }
            if (!options.CurrentValue.Enabled) throw new ServiceException(503, "logibooks_disabled");
            var orders = await ResolveAsync(request.Orders, true, token);
            var payload = await MapAsync(orders, request.Orders, label, LogibooksMapping.NormalizeRegister(request.Register), token);
            var serialized = LogibooksMapping.Serialize(payload);
            if (request.MappingVersion != LogibooksMapping.Version || request.Fingerprint != LogibooksMapping.Hash(serialized))
                throw new ServiceException(409, "logibooks_preview_changed");
            var actor = await ActorAsync(actorId, token);
            var now = clock.GetUtcNow();
            var operation = new LogibooksRegister
            {
                Id = request.OperationId,
                FileName = label,
                IntentHash = hash,
                Request = serialized,
                MappingVersion = LogibooksMapping.Version,
                Target = options.CurrentValue.Target,
                ActorId = actorId,
                ActorName = actor,
                CreatedAt = now,
                UpdatedAt = now,
                Rows = orders.Select((order, index) => new LogibooksRegisterRow
                {
                    RowNumber = index + 1,
                    OrderId = order.Id,
                    OrderNumber = request.Orders[index].OrderNumber,
                    ExpectedUpdatedAt = request.Orders[index].ExpectedUpdatedAt.ToUniversalTime()
                }).ToArray()
            };
            database.Add(operation);
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return await GetCoreAsync(operation.Id, token);
        }, token);

    private static string Normalize(Guid id, string? label, LogibooksOrderSelection[]? selection)
    {
        if (id == Guid.Empty) throw Invalid("operationId", "Обновите предварительный просмотр.");
        label = label?.Trim();
        if (string.IsNullOrEmpty(label) || label.Length > LogibooksMapping.MaximumLabel || label.Any(char.IsControl))
            throw Invalid("fileName", "Укажите название реестра длиной от 1 до 200 символов.");
        if (selection is null || selection.Length is < 1 or > LogibooksMapping.MaximumOrders
            || selection.Any(x => x is null || x.ExpectedUpdatedAt == default || string.IsNullOrEmpty(x.OrderNumber))
            || selection.Select(x => x.OrderNumber).Distinct(StringComparer.Ordinal).Count() != selection.Length)
            throw Invalid("orders", "Выберите от 1 до 1000 разных оплаченных заказов с актуальными версиями.");
        return label;
    }

    private async Task<Order[]> ResolveAsync(LogibooksOrderSelection[] selection, bool locked, CancellationToken token)
    {
        var predicate = PublicOrderNumbers.Selection(selection.Select(x => x.OrderNumber));
        if (locked)
        {
            var ids = await database.Orders.AsNoTracking().Where(predicate).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(token);
            await AppDatabaseOperations.For(database).LockOrdersAsync(database, ids, token);
        }
        var found = await database.Orders.AsNoTracking().Include(x => x.Customer).Where(predicate).ToArrayAsync(token);
        var byNumber = found.ToDictionary(LogibooksMapping.Number, StringComparer.Ordinal);
        return selection.Select(x => byNumber.GetValueOrDefault(x.OrderNumber) ?? throw new ServiceException(404, "resource_not_found")).ToArray();
    }

    private async Task<LogibooksPayload> MapAsync(Order[] orders, LogibooksOrderSelection[] selection, string label, LogibooksRegisterInput? details, CancellationToken token)
    {
        var ids = orders.Select(x => x.Id).ToArray();
        var blocked = await database.Set<LogibooksRegisterRow>().Where(x => ids.Contains(x.OrderId) && (x.Blocking || x.Register.InvestigationRequired))
            .Select(x => x.OrderId).ToArrayAsync(token);
        for (var index = 0; index < orders.Length; index++)
        {
            var order = orders[index];
            if (order.UpdatedAt != selection[index].ExpectedUpdatedAt
                || LogibooksMapping.Ineligible(order.Status, order.SellerPrice, order.SellerPriceCurrency, blocked.Contains(order.Id)) is not null)
                throw new ServiceException(409, "logibooks_preview_changed");
        }
        var goods = orders.Select(LogibooksMapping.Goods).ToArray();
        var payload = new LogibooksPayload(new(label, goods.Sum(x => x.WeightKg * x.Quantity),
            DealNumber: details?.DealNumber, CustomerOrderNumber: details?.CustomerOrderNumber,
            InvoiceNumber: details?.InvoiceNumber, InvoiceDate: details?.InvoiceDate), goods);
        if (Encoding.UTF8.GetByteCount(LogibooksMapping.Serialize(payload)) > options.CurrentValue.OutboundBytes)
            throw new ServiceException(413, "request_too_large");
        return payload;
    }

    private async Task<string> ActorAsync(int id, CancellationToken token)
    {
        var actor = await database.BackofficeUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token)
            ?? throw new ServiceException(404, "backoffice_user_not_found");
        return string.Join(' ', new[] { actor.LastName, actor.FirstName, actor.Patronymic }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    internal static readonly Expression<Func<LogibooksRegister, LogibooksOperationDto>> Projection = x => new(
        x.Id, x.Version, x.FileName, x.State.ToString(), x.Phase.ToString(), x.CreatedAt, x.UpdatedAt, x.AttemptId,
        x.Rows.Count, x.Rows.Count(row => row.Outcome == LogibooksRowOutcome.Created),
        x.Rows.Count(row => row.Outcome == LogibooksRowOutcome.Rejected),
        x.Rows.Count(row => row.Outcome == LogibooksRowOutcome.Unresolved), x.RemoteRegisterId,
        x.InvestigationRequired, x.Failure == null ? null : SarafanProblemDetailsFactory.ReadRetainedLogibooksFailure(x.Failure));

    private async Task<LogibooksOperationDto> GetCoreAsync(Guid id, CancellationToken token)
        => await database.Set<LogibooksRegister>().AsNoTracking().Where(x => x.Id == id).Select(Projection).SingleOrDefaultAsync(token)
            ?? throw new ServiceException(404, "resource_not_found");

    public Task<LogibooksOperationDto> GetAsync(Guid id, string[] roles, CancellationToken token)
        => Run(nameof(GetAsync), () => LogValueSummary.Inputs((nameof(id), id), (nameof(roles), roles), (nameof(token), token)),
            () => { Authorize(roles); return GetCoreAsync(id, token); }, token);

    public Task<LogibooksMetadata> DetailsAsync(Guid id, string[] roles, CancellationToken token)
        => Run(nameof(DetailsAsync), () => LogValueSummary.Inputs((nameof(id), id), (nameof(roles), roles), (nameof(token), token)), async () =>
        {
            Authorize(roles);
            var payload = await database.Set<LogibooksRegister>().AsNoTracking().Where(x => x.Id == id)
                .Select(x => x.Request).SingleOrDefaultAsync(token) ?? throw new ServiceException(404, "resource_not_found");
            return LogibooksMapping.Read<LogibooksPayload>(payload).Register;
        }, token);

    public Task<PagedResult<LogibooksOperationDto>> ListAsync(int page, int pageSize, string? search, string? state,
        string sortBy, string sortOrder, string[] roles, CancellationToken token)
        => Run(nameof(ListAsync), () => LogValueSummary.Inputs((nameof(page), page), (nameof(pageSize), pageSize),
            (nameof(search), search), (nameof(state), state), (nameof(sortBy), sortBy), (nameof(sortOrder), sortOrder),
            (nameof(roles), roles), (nameof(token), token)), async () =>
        {
            Authorize(roles);
            ValidatePage(page, pageSize);
            search = search?.Trim();
            if (search?.Length > 200 || sortBy is not ("createdAt" or "fileName" or "state") || sortOrder is not ("asc" or "desc"))
                throw new ServiceException(400, "invalid_order_list_filter");
            var query = database.Set<LogibooksRegister>().AsNoTracking();
            if (!string.IsNullOrEmpty(state))
            {
                if (!Enum.TryParse<LogibooksState>(state, out var value) || value.ToString() != state)
                    throw new ServiceException(400, "invalid_order_list_filter");
                query = query.Where(x => x.State == value);
            }
            if (!string.IsNullOrEmpty(search)) query = ListDisplaySearch.Apply(query, search,
                x => x.FileName, ListDisplaySearch.Label<LogibooksRegister, LogibooksState>(x => x.State, StateName),
                x => AppDbContext.SearchDate(AppDbContext.SearchLocalTime("Europe/Moscow", x.CreatedAt), "DD.MM.YYYY, HH24:MI") + " МСК",
                x => x.Rows.Count.ToString(), x => x.Rows.Count(row => row.Outcome == LogibooksRowOutcome.Created).ToString(),
                x => x.Rows.Count(row => row.Outcome == LogibooksRowOutcome.Rejected).ToString(),
                x => x.Rows.Count(row => row.Outcome == LogibooksRowOutcome.Unresolved).ToString(),
                x => x.RemoteRegisterId == null ? "—" : x.RemoteRegisterId.Value.ToString());
            var count = await query.CountAsync(token);
            var ordered = (sortBy, sortOrder) switch
            {
                ("fileName", "asc") => query.OrderBy(x => x.FileName),
                ("fileName", _) => query.OrderByDescending(x => x.FileName),
                ("state", "asc") => query.OrderBy(x => x.State),
                ("state", _) => query.OrderByDescending(x => x.State),
                (_, "asc") => query.OrderBy(x => x.CreatedAt),
                _ => query.OrderByDescending(x => x.CreatedAt)
            };
            var offset = (long)(page - 1) * pageSize;
            var items = offset > int.MaxValue ? [] : await ordered.ThenBy(x => x.Id).Skip((int)offset).Take(pageSize).Select(Projection).ToArrayAsync(token);
            return Page(items, count, page, pageSize, search, sortBy, sortOrder);
        }, token);

    public Task<PagedResult<LogibooksRowDto>> RowsAsync(Guid id, int page, int pageSize, string? search, string[] roles, CancellationToken token)
        => Run(nameof(RowsAsync), () => LogValueSummary.Inputs((nameof(id), id), (nameof(page), page), (nameof(pageSize), pageSize), (nameof(search), search),
            (nameof(roles), roles), (nameof(token), token)), async () =>
        {
            Authorize(roles);
            ValidatePage(page, pageSize);
            var payload = await database.Set<LogibooksRegister>().AsNoTracking().Where(x => x.Id == id).Select(x => x.Request).SingleOrDefaultAsync(token)
                ?? throw new ServiceException(404, "resource_not_found");
            search = search?.Trim();
            if (search?.Length > 200) throw new ServiceException(400, "invalid_order_list_filter");
            var query = string.IsNullOrEmpty(search)
                ? database.Set<LogibooksRegisterRow>().Where(x => x.RegisterId == id)
                : AppDatabaseOperations.For(database).SearchLogibooksRows(database, id, search);
            var count = await query.CountAsync(token);
            var goods = LogibooksMapping.Read<LogibooksPayload>(payload).Parcels;
            var offset = (long)(page - 1) * pageSize;
            var rows = offset > int.MaxValue ? [] : await query.AsNoTracking().OrderBy(x => x.RowNumber).Skip((int)offset).Take(pageSize).ToArrayAsync(token);
            return Page(rows.Select(x => new LogibooksRowDto(x.RowNumber, x.OrderNumber, x.Outcome.ToString(), x.ParcelId,
                x.StatusId, x.CheckStatus, LogibooksMapping.Read<LogibooksIssue[]>(x.Issues), goods[x.RowNumber - 1])).ToArray(),
                count, page, pageSize, search, "rowNumber", "asc");
        }, token);

    private static void ValidatePage(int page, int pageSize)
    {
        if (page < 1 || pageSize is not (10 or 25 or 50 or 100)) throw new ServiceException(400, "invalid_order_list_filter");
    }
    private static PagedResult<T> Page<T>(T[] items, int count, int page, int size, string? search, string sort, string direction)
        => new()
        {
            Items = items,
            Search = search,
            Sorting = new() { SortBy = sort, SortOrder = direction },
            Pagination = new()
            {
                CurrentPage = page,
                PageSize = size,
                TotalCount = count,
                TotalPages = (int)Math.Ceiling(count / (double)size),
                HasNextPage = (long)page * size < count,
                HasPreviousPage = page > 1
            }
        };
}
