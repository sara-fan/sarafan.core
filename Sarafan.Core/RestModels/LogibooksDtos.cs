// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.RestModels;

public sealed record LogibooksOrderSelection(string OrderNumber, DateTimeOffset ExpectedUpdatedAt);
public sealed record LogibooksRegisterInput(string? DealNumber = null, string? CustomerOrderNumber = null,
    string? InvoiceNumber = null, DateOnly? InvoiceDate = null);
public sealed record LogibooksPreviewRequest(Guid OperationId, string? FileName, LogibooksOrderSelection[] Orders,
    LogibooksRegisterInput? Register = null);
public sealed record LogibooksCreateRequest(Guid OperationId, string FileName, LogibooksOrderSelection[] Orders,
    int MappingVersion, string Fingerprint, LogibooksRegisterInput? Register = null);
public sealed record LogibooksPreviewDto(Guid OperationId, string FileName, int MappingVersion, string Fingerprint,
    LogibooksMetadata Register, LogibooksGoods[] Parcels, string[] DemoDefaults);
public sealed record LogibooksMetadata(string FileName, decimal RealWeightKg,
    int CustomsProcedureCode = 40, int TransportationTypeCode = 1, int IncotermsCode = 2,
    string? DealNumber = null, string? CustomerOrderNumber = null, string? InvoiceNumber = null, DateOnly? InvoiceDate = null);
public sealed record LogibooksGoods(string PostingNumber, string ProductName, decimal UnitPrice, int Quantity,
    string ProductLink, string? Phone, string? LastName = null, string? FirstName = null, string? Patronymic = null,
    string? Email = null, string? PassportSeries = null, string? PassportNumber = null,
    DateOnly? PassportIssueDate = null, string? PassportIssuedBy = null, string? Inn = null,
    string? PostalCode = null, string? City = null, string? Address = null,
    decimal WeightKg = 1.000m, string Currency = "USD", string TnVed = "1000000000",
    int CountryCode = 643, int SenderCountryCode = 643, int TradingCountryCode = 643, string Sender = "Sarafan Demo",
    string? SenderPhone = null, string? SenderEmail = null, string? SenderAddress = null);
public sealed record LogibooksPayload(LogibooksMetadata Register, LogibooksGoods[] Parcels);
public sealed record LogibooksIssue(string Code, string? Field, string Msg);
public sealed record LogibooksRemoteRow(int RowNumber, string? PostingNumber, string Outcome,
    int? ParcelId, int? StatusId, int? CheckStatus, LogibooksIssue[] Issues);
public sealed record LogibooksRemoteResult(int? RegisterId, int Processed, int Created, int Failed, LogibooksRemoteRow[] Rows);
public sealed record LogibooksRemoteReceipt(LogibooksRemoteResult? Result, bool Definitive, string? FailureCode);
public sealed record LogibooksOperationDto(Guid Id, Guid Version, string FileName, string State, string Phase,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? AttemptId, int Total, int Created, int Rejected,
    int Unresolved, int? RegisterId, bool InvestigationRequired, SarafanProblemDetails? Failure);
public sealed record LogibooksRowDto(int RowNumber, string OrderNumber, string Outcome, int? ParcelId,
    int? StatusId, int? CheckStatus, LogibooksIssue[] Issues, LogibooksGoods Goods);
public sealed record LogibooksReconcileRow(int RowNumber, bool Created, int? ParcelId);
public sealed record LogibooksReconcileRequest(Guid Version, Guid AttemptId, int? RegisterId,
    bool OriginalAttemptFinished, string Note, LogibooksReconcileRow[] Rows);
public sealed record LogibooksEligibilityDto(bool CanSelect, string? Reason, Guid? OperationId);
public sealed record LogibooksOpsDto(bool Enabled, bool CanManage, int MaximumOrders, int MaximumLabelLength,
    int MaximumCommandBytes, int MaximumOutboundBytes, int MaximumResponseBytes, int MappingVersion,
    string[] DemoDefaults, EnumOpsItemDto[] States, CurrencyOpsItemDto UnitPriceCurrency);
