// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

using Sarafan.Core.Data;
using Sarafan.Core.Models;

namespace Sarafan.Core.Authentication;

public static class CustomerPolicies
{
    public const string Access = "customer:access";
}

public sealed class CustomerAccessRequirement : IAuthorizationRequirement;

public sealed class CustomerAccessHandler(AppDbContext database) : AuthorizationHandler<CustomerAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CustomerAccessRequirement requirement)
    {
        if (!string.Equals(context.User.FindFirstValue("role"), "customer", StringComparison.Ordinal)
            || !int.TryParse(context.User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var customerId))
        {
            return;
        }

        var versionClaim = context.User.FindFirstValue(JwtTokenService.TokenVersionClaim);
        if (versionClaim is not null && !int.TryParse(versionClaim, out _)) return;
        var tokenVersion = versionClaim is null ? 0 : int.Parse(versionClaim);

        if (await database.Customers.AsNoTracking().AnyAsync(item =>
                item.Id == customerId
                && item.State != CustomerState.Disabled
                && item.TokenVersion == tokenVersion))
        {
            context.Succeed(requirement);
        }
    }
}

public static class CustomerAuthorization
{
    public static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy(CustomerPolicies.Access, policy => policy
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new CustomerAccessRequirement()));
        options.DefaultPolicy = options.GetPolicy(CustomerPolicies.Access)!;
    }
}
