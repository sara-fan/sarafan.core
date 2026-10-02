// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json.Serialization;
using Sarafan.Core.Models;
namespace Sarafan.Core.RestModels;

public sealed record CustomerDeliveryAddress(string? PostalCode, string? City, string? Address)
{
    public static CustomerDeliveryAddress From(Customer customer) => new(customer.PostalCode, customer.City, customer.Address);
    public CustomerDeliveryAddress Normalize() => new(Text(PostalCode), Text(City), Text(Address));
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        Check("postalCode", PostalCode, 20, "Укажите индекс.");
        Check("city", City, 150, "Укажите регион и населённый пункт.");
        Check("address", Address, 500, "Укажите адрес.");
        return errors;
        void Check(string field, string? value, int maximum, string required)
        {
            if (string.IsNullOrWhiteSpace(value)) errors[field] = [required];
            else if (value.Length > maximum) errors[field] = [$"Длина поля не должна превышать {maximum} символов."];
        }
    }
    [JsonIgnore] public string Destination => string.Join(", ", PostalCode, City, Address);
}
