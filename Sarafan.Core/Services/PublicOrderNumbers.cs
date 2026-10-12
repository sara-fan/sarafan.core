// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Globalization;
using System.Linq.Expressions;
using Sarafan.Core.Models;
namespace Sarafan.Core.Services;

internal static class PublicOrderNumbers
{
    internal static (string Code, long Sequence) Parse(string number)
    {
        var parts = (number ?? "").Split('-');
        if (parts.Length != 2 || parts[0].Length != 8 || parts[0].Any(c => c is < '0' or > '9')
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || sequence <= 0 || parts[1] != sequence.ToString(CultureInfo.InvariantCulture))
            throw new ServiceException(404, "resource_not_found");
        return (parts[0], sequence);
    }

    // Grouped, balanced predicates avoid cross-product reads and thousand-level expression trees.
    internal static Expression<Func<Order, bool>> Selection(IEnumerable<string> numbers)
    {
        var parameter = Expression.Parameter(typeof(Order), "order");
        var groups = numbers.Select(Parse).GroupBy(x => x.Code).Select(group =>
            (Expression)Expression.AndAlso(
                Expression.Equal(Expression.Property(Expression.Property(parameter, nameof(Order.Customer)), nameof(Customer.OrderCode)),
                    Expression.Constant(group.Key)),
                Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(long)],
                    Expression.Constant(group.Select(x => x.Sequence).ToArray()),
                    Expression.Property(parameter, nameof(Order.CustomerOrderNumber))))).ToList();
        if (groups.Count == 0) return _ => false;
        while (groups.Count > 1)
            groups = groups.Chunk(2).Select(pair => pair.Length == 1 ? pair[0] : Expression.OrElse(pair[0], pair[1])).ToList();
        return Expression.Lambda<Func<Order, bool>>(groups[0], parameter);
    }
}
