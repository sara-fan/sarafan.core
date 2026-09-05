// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Authentication;

public sealed record BackofficeRoleDefinition(string Code, string DisplayName);

public static class BackofficeRoles
{
    public const string Administrator = "administrator";
    public const string ShiftManager = "shift-manager";
    public const string SeniorOperator = "senior-operator";
    public const string Operator = "operator";

    public static readonly IReadOnlyList<BackofficeRoleDefinition> Definitions =
    [
        new(Administrator, "Administrator"),
        new(ShiftManager, "Shift manager"),
        new(SeniorOperator, "Senior operator"),
        new(Operator, "Operator")
    ];

    public static readonly IReadOnlySet<string> Codes = Definitions
        .Select(item => item.Code)
        .ToHashSet(StringComparer.Ordinal);
}
