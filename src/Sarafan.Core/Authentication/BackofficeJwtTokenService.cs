// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using Sarafan.Core.Models;
using Sarafan.Core.Observability;

namespace Sarafan.Core.Authentication;

public sealed class BackofficeJwtTokenService(
    IOptions<BackofficeAuthenticationOptions> options,
    TimeProvider timeProvider,
    ILogger<BackofficeJwtTokenService> logger)
{
    public const string IdentityTypeClaim = "identity_type";
    public const string IdentityType = "backoffice";
    public const string TokenVersionClaim = "token_version";

    private readonly BackofficeAuthenticationOptions _options = options.Value;
    private readonly SymmetricSecurityKey _signingKey = new(Encoding.UTF8.GetBytes(options.Value.SigningKey));

    public AccessTokenResult CreateAccessToken(BackofficeUser user)
        => OperationLogging.Run(
            logger,
            $"{typeof(BackofficeJwtTokenService).FullName}.{nameof(CreateAccessToken)}",
            () => LogValueSummary.Inputs((nameof(user), user)),
            () => CreateAccessTokenCore(user));

    private AccessTokenResult CreateAccessTokenCore(BackofficeUser user)
    {
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new(IdentityTypeClaim, IdentityType),
            new(TokenVersionClaim, user.TokenVersion.ToString())
        };
        claims.AddRange(user.UserRoles
            .OrderBy(item => item.RoleCode, StringComparer.Ordinal)
            .Select(item => new Claim("role", item.RoleCode)));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256));

        return new AccessTokenResult(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public static TokenValidationParameters CreateValidationParameters(
        BackofficeAuthenticationOptions options,
        SecurityKey signingKey) => new()
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = "role"
        };
}
