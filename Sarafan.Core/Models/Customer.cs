// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class Customer
{
    public int Id { get; set; }
    public required string Phone { get; set; }
    public string? OrderCode { get; private set; }
    public long NextOrderNumber { get; private set; } = 1;
    public CustomerState State { get; set; } = CustomerState.Preliminary;
    public int TokenVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

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
    public CustomerPhoto? Photo { get; set; }
    public ICollection<ConsentEvent> ConsentEvents { get; set; } = [];
    public ICollection<RefreshSession> RefreshSessions { get; set; } = [];
    public ICollection<Order> Orders { get; set; } = [];

    internal long AllocateOrderNumber(string generatedOrderCode)
    {
        if (OrderCode is null)
        {
            if (generatedOrderCode.Length != 8 || generatedOrderCode.Any(character => character is < '0' or > '9'))
            {
                throw new ArgumentException("Customer order codes must contain exactly eight digits.", nameof(generatedOrderCode));
            }

            OrderCode = generatedOrderCode;
        }

        if (NextOrderNumber <= 0)
        {
            throw new InvalidOperationException("The next customer order number must be positive.");
        }

        var orderNumber = NextOrderNumber;
        NextOrderNumber = checked(NextOrderNumber + 1);
        return orderNumber;
    }
}
