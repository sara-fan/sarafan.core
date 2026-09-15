// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Models;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

internal static class OrderProductRules
{
    internal static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static OrderProductDto Normalize(SubmittedProductRequest? product, int quantity, string? comment)
        => new(Text(product?.ProductName), product?.SellerPrice, quantity,
            Text(product?.Color), Text(product?.Size), Text(comment));

    internal static void Validate(OrderProductDto product)
    {
        if (product.Quantity <= 0) throw new ServiceException(400, "invalid_order_quantity");
        if (product.Quantity > 4) throw new ServiceException(400, "order_quantity_limit_exceeded");
        if (product.ProductName is null or { Length: > 500 }) throw new ServiceException(400, "invalid_order_product_name");
        if (product.SellerPrice is not { Currency: Currency.Usd, Amount: > 0 and <= 99999999.99m } price
            || decimal.Round(price.Amount, 2) != price.Amount)
            throw new ServiceException(400, "invalid_order_seller_price");
        if (product.Color?.Length > 200) throw new ServiceException(400, "invalid_order_color");
        if (product.Size?.Length > 200) throw new ServiceException(400, "invalid_order_size");
        if (product.Comment?.Length > 2000) throw new ServiceException(400, "invalid_order_comment");
    }
}
