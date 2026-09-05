// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;

namespace Sarafan.Core.Authentication;

public enum BackofficeAction
{
    Access = 0,
    ManageUsers = 1,
    ManageRoles = 2,
    OperationalQueue = 3,
    ManualQuotes = 4,
    ManageLegalDocuments = 5
}

public static class BackofficeAuthenticationDefaults
{
    public const string Scheme = "BackofficeBearer";
}

public static class BackofficePolicies
{
    public const string Access = "backoffice:access";
    public const string ManageUsers = "backoffice:manage-users";
    public const string ManageRoles = "backoffice:manage-roles";
    public const string OperationalQueue = "backoffice:operational-queue";
    public const string ManualQuotes = "backoffice:manual-quotes";
    public const string ManageLegalDocuments = "backoffice:manage-legal-documents";
    public const string Administrator = "backoffice:role:administrator";
    public const string ShiftManager = "backoffice:role:shift-manager";
    public const string SeniorOperator = "backoffice:role:senior-operator";
    public const string Operator = "backoffice:role:operator";
}

public static class BackofficeAuthorization
{
    private static readonly IReadOnlyDictionary<BackofficeAction, IReadOnlySet<string>> AllowedRoles =
        new Dictionary<BackofficeAction, IReadOnlySet<string>>
        {
            [BackofficeAction.Access] = new HashSet<string>(BackofficeRoles.Codes, StringComparer.Ordinal),
            [BackofficeAction.ManageUsers] = new HashSet<string>([BackofficeRoles.Administrator], StringComparer.Ordinal),
            [BackofficeAction.ManageRoles] = new HashSet<string>([BackofficeRoles.Administrator], StringComparer.Ordinal),
            [BackofficeAction.OperationalQueue] = new HashSet<string>(
                [BackofficeRoles.Administrator, BackofficeRoles.ShiftManager],
                StringComparer.Ordinal),
            [BackofficeAction.ManualQuotes] = new HashSet<string>(BackofficeRoles.Codes, StringComparer.Ordinal),
            [BackofficeAction.ManageLegalDocuments] = new HashSet<string>(
                [BackofficeRoles.Administrator],
                StringComparer.Ordinal)
        };

    public static bool IsAllowed(IEnumerable<string> roles, BackofficeAction action)
        => AllowedRoles.TryGetValue(action, out var allowed)
            && roles.Any(allowed.Contains);

    public static void Configure(AuthorizationOptions options)
    {
        AddPolicy(options, BackofficePolicies.Access, BackofficeAction.Access);
        AddPolicy(options, BackofficePolicies.ManageUsers, BackofficeAction.ManageUsers);
        AddPolicy(options, BackofficePolicies.ManageRoles, BackofficeAction.ManageRoles);
        AddPolicy(options, BackofficePolicies.OperationalQueue, BackofficeAction.OperationalQueue);
        AddPolicy(options, BackofficePolicies.ManualQuotes, BackofficeAction.ManualQuotes);
        AddPolicy(options, BackofficePolicies.ManageLegalDocuments, BackofficeAction.ManageLegalDocuments);
        AddRolePolicy(options, BackofficePolicies.Administrator, BackofficeRoles.Administrator);
        AddRolePolicy(options, BackofficePolicies.ShiftManager, BackofficeRoles.ShiftManager);
        AddRolePolicy(options, BackofficePolicies.SeniorOperator, BackofficeRoles.SeniorOperator);
        AddRolePolicy(options, BackofficePolicies.Operator, BackofficeRoles.Operator);
    }

    private static void AddPolicy(AuthorizationOptions options, string name, BackofficeAction action)
        => options.AddPolicy(name, policy => policy
            .AddAuthenticationSchemes(BackofficeAuthenticationDefaults.Scheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(context => IsAllowed(
                context.User.FindAll("role").Select(claim => claim.Value),
                action)));

    private static void AddRolePolicy(AuthorizationOptions options, string name, string role)
        => options.AddPolicy(name, policy => policy
            .AddAuthenticationSchemes(BackofficeAuthenticationDefaults.Scheme)
            .RequireAuthenticatedUser()
            .RequireRole(role));
}
