// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class CustomerProfile
{
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public string? LastName { get; set; }
    public string? FirstName { get; set; }
    public string? Patronymic { get; set; }
    public string? Email { get; set; }
    public string? PassportSeries { get; set; }
    public string? PassportNumber { get; set; }
    public DateOnly? PassportIssueDate { get; set; }
    public string? PassportIssuedBy { get; set; }
    public string? Inn { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string? Address { get; set; }
}
