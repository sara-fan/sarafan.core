// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public enum LogibooksState { Submitting, Succeeded, PartiallySucceeded, Rejected, Unknown }
public enum LogibooksPhase { Queued, Dispatching, ReceiptRetained, Applied }
public enum LogibooksRowOutcome { Unresolved, Created, Rejected }

public sealed class LogibooksRegister
{
    public Guid Id { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = "";
    public string Target { get; set; } = "";
    public int MappingVersion { get; set; }
    public string IntentHash { get; set; } = "";
    public string Request { get; set; } = "";
    public LogibooksState State { get; set; }
    public LogibooksPhase Phase { get; set; }
    public int ActorId { get; set; }
    public string ActorName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? AttemptId { get; set; }
    public DateTimeOffset? Deadline { get; set; }
    public bool AuthenticationRetried { get; set; }
    public bool InvestigationRequired { get; set; }
    public int? RemoteRegisterId { get; set; }
    public string? Failure { get; set; }
    public ICollection<LogibooksRegisterRow> Rows { get; set; } = [];
    internal void Touch(DateTimeOffset now) { Version = Guid.NewGuid(); UpdatedAt = now; }
}

public sealed class LogibooksRegisterRow
{
    public Guid RegisterId { get; set; }
    public LogibooksRegister Register { get; set; } = null!;
    public int RowNumber { get; set; }
    public long OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public string OrderNumber { get; set; } = "";
    public DateTimeOffset ExpectedUpdatedAt { get; set; }
    public bool Blocking { get; set; } = true;
    public LogibooksRowOutcome Outcome { get; set; }
    public int? ParcelId { get; set; }
    public int? StatusId { get; set; }
    public int? CheckStatus { get; set; }
    public string Issues { get; set; } = "[]";
    public long? AcceptanceHistoryEventId { get; set; }
    public OrderHistoryEvent? AcceptanceHistoryEvent { get; set; }
}

public sealed class LogibooksReceipt
{
    public long Id { get; set; }
    public Guid RegisterId { get; set; }
    public LogibooksRegister Register { get; set; } = null!;
    public Guid AttemptId { get; set; }
    public DateTimeOffset At { get; set; }
    public string Hash { get; set; } = "";
    public string Payload { get; set; } = "";
    public int? ReconcilerId { get; set; }
    public string? ReconcilerName { get; set; }
}

public sealed class LogibooksReconciliation
{
    public long Id { get; set; }
    public Guid RegisterId { get; set; }
    public LogibooksRegister Register { get; set; } = null!;
    public Guid AttemptId { get; set; }
    public int ActorId { get; set; }
    public string ActorName { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string Note { get; set; } = "";
    public string Payload { get; set; } = "";
}
