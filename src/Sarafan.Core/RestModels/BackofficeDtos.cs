// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;

using Sarafan.Core.Authentication;
using Sarafan.Core.Models;

namespace Sarafan.Core.RestModels;

public sealed class BackofficeLoginRequest
{
    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [EmailAddress(ErrorMessage = "Укажите корректный адрес электронной почты.")]
    [StringLength(254, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    public string Password { get; set; } = string.Empty;
}

public sealed record BackofficeAuthenticationSessionDto(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    BackofficeIdentityDto User);

public sealed record BackofficeRoleDto(string Code, string DisplayName)
{
    public static BackofficeRoleDto From(BackofficeRole role) => new(role.Code, role.DisplayName);
}

public sealed record BackofficeUserDto(
    int Id,
    string Email,
    string FirstName,
    string LastName,
    string? Patronymic,
    bool IsActive,
    bool IsDemo,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static BackofficeUserDto From(BackofficeUser user) => new(
        user.Id,
        user.Email,
        user.FirstName,
        user.LastName,
        user.Patronymic,
        user.IsActive,
        user.IsDemo,
        user.UserRoles.Select(item => item.RoleCode).Order(StringComparer.Ordinal).ToArray(),
        user.CreatedAt,
        user.UpdatedAt);
}

public sealed record BackofficeIdentityDto(
    int Id,
    string Email,
    string FirstName,
    string LastName,
    string? Patronymic,
    IReadOnlyList<string> Roles)
{
    public static BackofficeIdentityDto From(BackofficeUser user) => new(
        user.Id,
        user.Email,
        user.FirstName,
        user.LastName,
        user.Patronymic,
        user.UserRoles.Select(item => item.RoleCode).Order(StringComparer.Ordinal).ToArray());
}

public sealed class BackofficeUserCreateRequest
{
    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [EmailAddress(ErrorMessage = "Укажите корректный адрес электронной почты.")]
    [StringLength(254, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Длина поля должна составлять от {2} до {1} символов.")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Длина поля должна составлять от {2} до {1} символов.")]
    public string LastName { get; set; } = string.Empty;

    [StringLength(100, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string? Patronymic { get; set; }

    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [BackofficePassword]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Укажите хотя бы одну роль.")]
    public IReadOnlyCollection<string> Roles { get; set; } = [];
}

public sealed class BackofficeUserUpdateRequest
{
    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [EmailAddress(ErrorMessage = "Укажите корректный адрес электронной почты.")]
    [StringLength(254, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Длина поля должна составлять от {2} до {1} символов.")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Длина поля должна составлять от {2} до {1} символов.")]
    public string LastName { get; set; } = string.Empty;

    [StringLength(100, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string? Patronymic { get; set; }

    [BackofficePassword]
    public string? Password { get; set; }

    public bool IsActive { get; set; } = true;

    [Required(ErrorMessage = "Укажите хотя бы одну роль.")]
    public IReadOnlyCollection<string> Roles { get; set; } = [];
}

public sealed class BackofficeSelfUpdateRequest
{
    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Длина поля должна составлять от {2} до {1} символов.")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Длина поля должна составлять от {2} до {1} символов.")]
    public string LastName { get; set; } = string.Empty;

    [StringLength(100, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string? Patronymic { get; set; }

    [BackofficePassword]
    public string? Password { get; set; }
}
