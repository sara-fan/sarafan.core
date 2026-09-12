// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;

using Sarafan.Core.Models;

namespace Sarafan.Core.RestModels;

public sealed record CustomerDto(
    int Id,
    string Phone,
    CustomerState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool HasPhoto,
    CustomerProfileDto Profile)
{
    public static CustomerDto From(Customer customer, bool hasPhoto) => new(
        customer.Id,
        customer.Phone,
        customer.State,
        customer.CreatedAt,
        customer.UpdatedAt,
        hasPhoto,
        CustomerProfileDto.From(customer.Profile, customer.Phone));
}

public sealed record CustomerOpsDto(IReadOnlyList<EnumOpsItemDto> States);

public sealed record CustomerProfileDto(
    string Phone,
    string? LastName,
    string? FirstName,
    string? Patronymic,
    string? Email,
    string? PassportSeries,
    string? PassportNumber,
    DateOnly? PassportIssueDate,
    string? PassportIssuedBy,
    string? Inn,
    string? PostalCode,
    string? City,
    string? Address)
{
    public static CustomerProfileDto From(CustomerProfile profile, string phone) => new(
        phone,
        profile.LastName,
        profile.FirstName,
        profile.Patronymic,
        profile.Email,
        profile.PassportSeries,
        profile.PassportNumber,
        profile.PassportIssueDate,
        profile.PassportIssuedBy,
        profile.Inn,
        profile.PostalCode,
        profile.City,
        profile.Address);
}

public sealed class CustomerProfileUpdateRequest
{
    [StringLength(100, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string? LastName { get; set; }

    [StringLength(100, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string? FirstName { get; set; }

    [StringLength(100, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string? Patronymic { get; set; }

    [EmailAddress(ErrorMessage = "Укажите корректный адрес электронной почты.")]
    [StringLength(254, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string? Email { get; set; }

    [StringLength(32, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string? PassportSeries { get; set; }

    [StringLength(32, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string? PassportNumber { get; set; }

    public DateOnly? PassportIssueDate { get; set; }

    [StringLength(500, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string? PassportIssuedBy { get; set; }

    [RegularExpression(
        "^(?:[0-9]{10}|[0-9]{12})?$",
        ErrorMessage = "ИНН должен содержать 10 или 12 цифр.")]
    public string? Inn { get; set; }

    [StringLength(20, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string? PostalCode { get; set; }

    [StringLength(150, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string? City { get; set; }

    [StringLength(500, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string? Address { get; set; }
}
