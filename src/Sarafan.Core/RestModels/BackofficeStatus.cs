// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.RestModels;

public sealed record ExchangeRateDto(
    string Provider, string BaseCurrency, string QuoteCurrency, int Nominal,
    decimal OfficialRate, DateOnly SourceEffectiveDate, DateTimeOffset RetrievedAt);

public sealed record BackofficeStatus(
    string Service, string Status, string AppVersion, IReadOnlyList<ExchangeRateDto> ExchangeRates);
