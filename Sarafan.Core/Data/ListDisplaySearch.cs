// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Linq.Expressions;
using Sarafan.Core.Models;
using Sarafan.Core.Services;

namespace Sarafan.Core.Data;

internal static class ListDisplaySearch
{
    internal const string MoneyPattern = "FM999,999,999,999,999,999,990.00";
    internal static string Normalize(string value) => value.Replace('\u00a0', ' ').Replace('\u202f', ' ').Trim().ToLowerInvariant();

    internal static IQueryable<T> Apply<T>(IQueryable<T> query, string search, params Expression<Func<T, string>>[] fields)
    {
        var term = Normalize(search);
        if (term.Length == 0) return query;
        var row = Expression.Parameter(typeof(T), "row");
        Expression predicate = Expression.Constant(false);
        foreach (var field in fields)
        {
            var value = new Substitute(field.Parameters[0], row).Visit(field.Body)!;
            Expression<Func<string, bool>> match = text => (text ?? "").Replace("\u00a0", " ").Replace("\u202f", " ").Trim().ToLower().Contains(term);
            predicate = Expression.OrElse(predicate, new Substitute(match.Parameters[0], value).Visit(match.Body)!);
        }
        return query.Where(Expression.Lambda<Func<T, bool>>(predicate, row));
    }

    internal static Expression<Func<T, string>> Label<T, TEnum>(Expression<Func<T, TEnum>> field, Func<TEnum, string> label)
        where TEnum : struct, Enum
    {
        Expression result = Expression.Constant("—");
        foreach (var value in Enum.GetValues<TEnum>())
            result = Expression.Condition(Expression.Equal(field.Body, Expression.Constant(value)), Expression.Constant(label(value)), result);
        return Expression.Lambda<Func<T, string>>(result, field.Parameters);
    }

    internal static IQueryable<Order> Orders(IQueryable<Order> query, string search)
        => Apply(query, search,
            row => row.Customer.OrderCode + "-" + row.CustomerOrderNumber.ToString(),
            Label<Order, OrderStatus>(row => row.Status, value => value.GetDisplayName()),
            row => row.ProductName == null || row.ProductName == "" ? "Товар не указан" : row.ProductName,
            row => row.StoreName == null || row.StoreName == "" ? "Магазин не указан" : row.StoreName,
            Money(), row => row.Quantity.ToString(),
            row => AppDbContext.SearchDate(AppDbContext.SearchLocalTime("Europe/Moscow", row.CreatedAt), "DD.MM.YYYY, HH24:MI") + " МСК",
            row => AppDbContext.SearchDate(AppDbContext.SearchLocalTime("Europe/Moscow", row.UpdatedAt), "DD.MM.YYYY, HH24:MI") + " МСК");

    private static Expression<Func<Order, string>> Money()
    {
        Expression<Func<Order, Currency>> currency = row => row.SellerPriceCurrency!.Value;
        var symbol = Label(currency, value => value.GetSymbol());
        Expression<Func<Order, string>> amount = row => AppDbContext.SearchMoney(row.SellerPrice!.Value, MoneyPattern).Replace(",", " ").Replace(".", ",");
        var combined = Expression.Add(amount.Body, new Substitute(symbol.Parameters[0], amount.Parameters[0]).Visit(symbol.Body)!,
            typeof(string).GetMethod("Concat", [typeof(string), typeof(string)]));
        var absent = Expression.Equal(Expression.Property(amount.Parameters[0], nameof(Order.SellerPrice)), Expression.Constant(null, typeof(decimal?)));
        return Expression.Lambda<Func<Order, string>>(Expression.Condition(absent, Expression.Constant("—"), combined), amount.Parameters);
    }

    internal static IQueryable<CustomerConsentWithdrawalRequest> Withdrawals(IQueryable<CustomerConsentWithdrawalRequest> query, string search)
        => Apply(query, search, row => "№ " + row.CustomerId.ToString(),
            row => row.Processed ? "Обработан" : "Ожидает ручной обработки",
            row => AppDbContext.SearchDate(AppDbContext.SearchLocalTime("Europe/Moscow", row.RequestedAt), "DD.MM.YYYY, HH24:MI") + " МСК");

    internal static IQueryable<LegalDocumentAuditEvent> LegalAudit(IQueryable<LegalDocumentAuditEvent> query, string search)
        => Apply(query, search,
            row => AppDbContext.SearchDate(AppDbContext.SearchLocalTime("Europe/Moscow", row.At), "DD.MM.YYYY, HH24:MI") + " МСК",
            row => row.Action == "created" ? "Создан" : row.Action == "deleted" ? "Удалён" : row.Action,
            row => row.Title, Label<LegalDocumentAuditEvent, LegalDocumentKind>(row => row.Kind, value => value.GetDisplayName()),
            row => row.DocumentId.ToString(), row => row.DisplayVersion,
            row => AppDbContext.SearchDate(AppDbContext.SearchLocalTime("Europe/Moscow", row.EffectiveAt), "DD.MM.YYYY"),
            LegalActor());

    private static Expression<Func<LegalDocumentAuditEvent, string>> LegalActor()
    {
        Expression<Func<LegalDocumentAuditEvent, string>> name = row =>
            ((string.IsNullOrWhiteSpace(row.BackofficeUser.LastName) ? "" : row.BackofficeUser.LastName + " ")
            + (string.IsNullOrWhiteSpace(row.BackofficeUser.FirstName) ? "" : row.BackofficeUser.FirstName + " ")
            + (string.IsNullOrWhiteSpace(row.BackofficeUser.Patronymic) ? "" : row.BackofficeUser.Patronymic)).Trim();
        Expression<Func<LegalDocumentAuditEvent, string>> fallback = row => "ID " + row.ActorId.ToString();
        return Expression.Lambda<Func<LegalDocumentAuditEvent, string>>(
            Expression.Condition(Expression.Equal(name.Body, Expression.Constant("")),
                new Substitute(fallback.Parameters[0], name.Parameters[0]).Visit(fallback.Body)!, name.Body), name.Parameters);
    }

    internal static IQueryable<OrderService.HistoryRow> History(IQueryable<OrderService.HistoryRow> query, string search)
    {
        var creation = OrderHistoryArea.Creation.GetDisplayName() + ", ";
        var product = OrderHistoryArea.Product.GetDisplayName() + ", ";
        var pricing = OrderHistoryArea.Pricing.GetDisplayName() + ", ";
        var status = OrderHistoryArea.Status.GetDisplayName() + ", ";
        return Apply(query, search,
            row => AppDbContext.SearchDate(AppDbContext.SearchLocalTime("Europe/Moscow", row.At), "DD.MM.YYYY, HH24:MI") + " МСК",
            Label<OrderService.HistoryRow, OrderHistoryKind>(row => row.Kind, value => value.GetDisplayName()),
            row => row.ActorName + (row.ActorNameHistorical ? "" : " (текущее имя)"),
            row => (((row.Areas & OrderHistoryArea.Creation) != 0 ? creation : "")
                + ((row.Areas & OrderHistoryArea.Product) != 0 ? product : "")
                + ((row.Areas & OrderHistoryArea.Pricing) != 0 ? pricing : "")
                + ((row.Areas & OrderHistoryArea.Status) != 0 ? status : "")).TrimEnd(',', ' '));
    }

    private sealed class Substitute(ParameterExpression parameter, Expression value) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parameter ? value : base.VisitParameter(node);
    }
}
