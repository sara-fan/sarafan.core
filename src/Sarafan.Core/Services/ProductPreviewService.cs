// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed class ProductPreviewService(ILogger<ProductPreviewService> logger)
{
    public ProductPreviewDto Preview(ProductPreviewRequest request)
        => OperationLogging.Run(
            logger,
            $"{typeof(ProductPreviewService).FullName}.{nameof(Preview)}",
            () => LogValueSummary.Inputs((nameof(request), request)),
            () => new ProductPreviewDto(
                ProductSourceUrl.Normalize(request.SourceUrl),
                ProductPreviewDto.ManualReviewOutcome));
}
