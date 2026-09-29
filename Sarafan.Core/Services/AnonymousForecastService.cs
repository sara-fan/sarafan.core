// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Data;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed class AnonymousForecastService(
    AppDbContext database, TimeProvider timeProvider, ILogger<AnonymousForecastService> logger)
{
    public Task<AnonymousForecastDto> CalculateAsync(AnonymousForecastRequest request, CancellationToken token)
        => OperationLogging.RunAsync(logger, $"{typeof(AnonymousForecastService).FullName}.{nameof(CalculateAsync)}",
            () => LogValueSummary.Inputs((nameof(request), request), (nameof(token), token)),
            async () =>
            {
                var price = request.SellerPrice is null ? null
                    : new OrderSellerPriceDto(request.SellerPrice.Amount, request.SellerPrice.Currency);
                OrderProductRules.ValidatePriceAndQuantity(price, request.Quantity);
                var result = await OrderPriceCalculator.CalculateForecastAsync(database,
                    price!, request.Quantity, timeProvider.GetUtcNow(), token);
                return new AnonymousForecastDto(result.CalculatedAt, result.TotalRub);
            }, token);
}
