// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using Sarafan.Core.Models;
using Sarafan.Core.Observability;

namespace Sarafan.Core.Authentication;

public sealed record AccessTokenResult(string Token, DateTimeOffset ExpiresAt);

public sealed class JwtTokenService(
    IOptions<AuthenticationOptions> options, TimeProvider timeProvider, ILogger<JwtTokenService> logger)
{
    public const string TokenVersionClaim = "token_version";
    private readonly AuthenticationOptions _options = options.Value;
    private readonly SymmetricSecurityKey _signingKey = new(Encoding.UTF8.GetBytes(options.Value.SigningKey));

    public AccessTokenResult CreateAccessToken(Customer customer)
        => OperationLogging.Run(logger, $"{typeof(JwtTokenService).FullName}.{nameof(CreateAccessToken)}",
            () => LogValueSummary.Inputs((nameof(customer), customer)),
            () => CreateAccessTokenCore(customer));

    private AccessTokenResult CreateAccessTokenCore(Customer customer)
    {
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, customer.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new("role", "customer"),
            new(TokenVersionClaim, customer.TokenVersion.ToString(), ClaimValueTypes.Integer32)
        };
        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return new AccessTokenResult(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public static TokenValidationParameters CreateValidationParameters(
        AuthenticationOptions options,
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

    public static string CreateRefreshToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    public static string HashRefreshToken(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
