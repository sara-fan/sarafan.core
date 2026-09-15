// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

using Sarafan.Core.Observability;
using Sarafan.Core.Models;

namespace Sarafan.Core.Services;

public sealed record CbrRate(DateOnly SourceEffectiveDate, int Nominal, decimal OfficialRate)
{
    public Currency BaseCurrency { get; init; } = Currency.Usd;
}

public interface ICbrRateClient
{
    Task<CbrRate> GetAsync(DateOnly date, CancellationToken cancellationToken);
    async Task<IReadOnlyList<CbrRate>> GetRatesAsync(DateOnly date, CancellationToken cancellationToken)
        => [await GetAsync(date, cancellationToken)];
}

public sealed partial class CbrRateClient(HttpClient httpClient, ILogger<CbrRateClient> logger) : ICbrRateClient
{
    public const string Endpoint = "https://www.cbr.ru/DailyInfoWebServ/DailyInfo.asmx";
    public const string SoapAction = "http://web.cbr.ru/GetCursOnDateXML";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Cbr = "http://web.cbr.ru/";

    public Task<CbrRate> GetAsync(DateOnly date, CancellationToken cancellationToken)
        => OperationLogging.RunAsync(logger, $"{typeof(CbrRateClient).FullName}.{nameof(GetAsync)}",
            () => LogValueSummary.Inputs((nameof(date), date), (nameof(cancellationToken), cancellationToken)),
            async () => (await GetRatesAsync(date, cancellationToken)).SingleOrDefault(rate => rate.BaseCurrency == Currency.Usd)
                ?? throw new InvalidDataException("No valid CBR USD rate."), cancellationToken);

    public Task<IReadOnlyList<CbrRate>> GetRatesAsync(DateOnly date, CancellationToken cancellationToken)
        => OperationLogging.RunAsync<IReadOnlyList<CbrRate>>(logger, $"{typeof(CbrRateClient).FullName}.{nameof(GetRatesAsync)}",
            () => LogValueSummary.Inputs((nameof(date), date), (nameof(cancellationToken), cancellationToken)),
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var envelope = new XDocument(new XElement(Soap + "Envelope",
                    new XAttribute(XNamespace.Xmlns + "soap", Soap),
                    new XElement(Soap + "Body", new XElement(Cbr + "GetCursOnDateXML",
                        new XElement(Cbr + "On_date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00")))));
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(envelope.ToString(), Encoding.UTF8, "text/xml")
                };
                request.Headers.Add("SOAPAction", SoapAction);
                // Intentionally buffer this small response: the configured HttpClient timeout and byte cap
                // cover the entire download. ResponseHeadersRead would require separate body limits/timeouts.
                using var response = await httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    Async = true,
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 1_048_576
                });
                var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var rates = new List<CbrRate>();
                foreach (var currency in new[] { Currency.Usd, Currency.Eur })
                {
                    try { rates.Add(Parse(document, date, currency)); }
                    catch (InvalidDataException) { /* Preserve each independently valid currency. */ }
                }
                if (rates.Count == 0) throw new InvalidDataException("No valid CBR rates.");
                return rates;
            }, cancellationToken);

    internal static CbrRate Parse(XDocument document, DateOnly requestedDate, Currency currency = Currency.Usd)
    {
        var data = document.Element(Soap + "Envelope")?.Element(Soap + "Body")?
            .Element(Cbr + "GetCursOnDateXMLResponse")?.Element(Cbr + "GetCursOnDateXMLResult")?
            .Element("ValuteData");
        if (!DateOnly.TryParseExact(data?.Attribute("OnDate")?.Value, "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var effectiveDate)
            || effectiveDate > requestedDate)
        {
            throw new InvalidDataException("Invalid CBR source-effective date.");
        }

        var alias = currency.GetRouteAlias();
        var code = ((int)currency).ToString(CultureInfo.InvariantCulture);
        var candidates = data!.Elements("ValuteCursOnDate").Where(row =>
            row.Element("VchCode")?.Value.Trim().Equals(alias, StringComparison.OrdinalIgnoreCase) == true
            || row.Element("Vcode")?.Value.Trim() == code).ToList();
        if (candidates.Count != 1)
        {
            throw new InvalidDataException("CBR response must contain one USD rate.");
        }

        var usd = candidates[0];
        var rawRate = usd.Element("Vcurs")?.Value.Trim();
        if (usd.Element("VchCode")?.Value.Trim().Equals(alias, StringComparison.OrdinalIgnoreCase) != true
            || usd.Element("Vcode")?.Value.Trim() != code
            || !int.TryParse(usd.Element("Vnom")?.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var nominal)
            || nominal is < 1 or > 1_000_000
            || rawRate is null || !RatePattern().IsMatch(rawRate)
            || !decimal.TryParse(rawRate.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var rate)
            || rate <= 0 || rate >= 1_000_000_000_000m)
        {
            throw new InvalidDataException("Invalid CBR USD rate.");
        }

        return new CbrRate(effectiveDate, nominal, rate) { BaseCurrency = currency };
    }

    [GeneratedRegex(@"\A[0-9]+([.,][0-9]{1,6})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex RatePattern();
}
