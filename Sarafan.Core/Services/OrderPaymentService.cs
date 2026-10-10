// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed class OrderPaymentService(OrderService orders, PaymentInformationService information,
    TimeProvider clock, ILogger<OrderPaymentService> logger)
{
    public Task<OrderPaymentDto> GetAsync(int customerId, string number, CancellationToken token)
        => OperationLogging.RunAsync(logger, typeof(OrderPaymentService).FullName + "." + nameof(GetAsync),
            () => LogValueSummary.Inputs((nameof(customerId), customerId), (nameof(number), number), (nameof(token), token)), async () =>
            {
                var order = await orders.GetAsync(customerId, number, token);
                var amount = order.Checkout is null ? null : MainPaymentAmount.From(order.Pricing);
                var eligible = order.Status == OrderStatus.QuoteReady && order.Checkout is not null && amount.HasValue
                    && order.Pricing.ValidUntil > clock.GetUtcNow();
                var bundle = eligible ? (await information.CurrentAsync(token)).PaymentInformation : null;
                var canPay = eligible && bundle is not null && order.Pricing.ValidUntil > clock.GetUtcNow();
                return new OrderPaymentDto(order, amount, canPay, canPay ? bundle : null);
            }, token);
}
