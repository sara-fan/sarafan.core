// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Globalization;
using System.Net;
using System.Xml;
using System.Xml.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed class CbrRateClientTests
{
    internal const string Row = "<ValuteCursOnDate><Vname>Доллар США </Vname><Vnom>1</Vnom><Vcurs>81.1234</Vcurs><Vcode>840</Vcode><VchCode>USD </VchCode><VunitRate>81.1234</VunitRate></ValuteCursOnDate>";
    private static readonly DateOnly Requested = new(2026, 9, 6);
    internal static string Soap(string rows = Row, string date = "20260905") =>
        $"<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><GetCursOnDateXMLResponse xmlns=\"http://web.cbr.ru/\"><GetCursOnDateXMLResult><ValuteData OnDate=\"{date}\" xmlns=\"\">{rows}</ValuteData></GetCursOnDateXMLResult></GetCursOnDateXMLResponse></soap:Body></soap:Envelope>";

    [TestCase("ru-RU", "81,1234")]
    [TestCase("en-US", "81.1234")]
    [TestCase("tr-TR", "81,1234")]
    public async Task RequestsSoapAndParsesInvariantOfficialRateAndSourceDate(string culture, string rate)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            using var http = new HttpClient(new Handler(async (request, _) =>
            {
                Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
                Assert.That(request.RequestUri!.AbsoluteUri, Is.EqualTo(CbrRateClient.Endpoint));
                Assert.That(request.Headers.GetValues("SOAPAction"), Is.EqualTo(new[] { CbrRateClient.SoapAction }));
                Assert.That(request.Content!.Headers.ContentType!.MediaType, Is.EqualTo("text/xml"));
                var body = XDocument.Parse(await request.Content.ReadAsStringAsync());
                Assert.That(body.Descendants(XName.Get("On_date", "http://web.cbr.ru/")).Single().Value,
                    Is.EqualTo("2026-09-06T00:00:00"));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Soap(Row.Replace("81.1234", rate).Replace("<Vnom>1", "<Vnom> 100 "))) };
            }));
            var result = await new CbrRateClient(http, NullLogger<CbrRateClient>.Instance).GetAsync(Requested, default);
            Assert.That(result, Is.EqualTo(new CbrRate(new DateOnly(2026, 9, 5), 100, 81.1234m)));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestCase("", "1")]
    [TestCase("0", "1")]
    [TestCase("-1", "1")]
    [TestCase("NaN", "1")]
    [TestCase("1e2", "1")]
    [TestCase("1,000.12", "1")]
    [TestCase("1.1234567", "1")]
    [TestCase("1000000000000", "1")]
    [TestCase("99999999999999999999999999999999999999999", "1")]
    [TestCase("81", "0")]
    [TestCase("81", "-1")]
    [TestCase("81", "1000001")]
    [TestCase("81", "1.5")]
    [TestCase("81", "")]
    public void RejectsInvalidAmountsAndNominals(string rate, string nominal)
        => Assert.Throws<InvalidDataException>(() => CbrRateClient.Parse(
            XDocument.Parse(Soap(Row.Replace("81.1234", rate).Replace("<Vnom>1</Vnom>", $"<Vnom>{nominal}</Vnom>"))), Requested));

    [TestCase("20260907")]
    [TestCase("20260230")]
    [TestCase("2026-09-05")]
    [TestCase("")]
    public void RejectsInvalidOrFutureSourceDates(string date)
        => Assert.Throws<InvalidDataException>(() => CbrRateClient.Parse(XDocument.Parse(Soap(date: date)), Requested));

    [Test]
    public void RejectsMissingDuplicateAndConflictingCurrencyFields()
    {
        foreach (var rows in new[] { "", Row + Row, Row.Replace("USD ", "EUR"), Row.Replace("840", "978"),
                     Row.Replace("<Vcode>840</Vcode>", ""), Row.Replace("<VchCode>USD </VchCode>", ""),
                     Row.Replace("<Vcurs>81.1234</Vcurs>", ""), Row.Replace("<Vnom>1</Vnom>", "") })
            Assert.Throws<InvalidDataException>(() => CbrRateClient.Parse(XDocument.Parse(Soap(rows)), Requested));
        foreach (var xml in new[] { "<Envelope/>", "<soap:Envelope xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'><soap:Body><soap:Fault/></soap:Body></soap:Envelope>", Soap().Replace("OnDate=\"20260905\"", "") })
            Assert.Throws<InvalidDataException>(() => CbrRateClient.Parse(XDocument.Parse(xml), Requested));
        Assert.That(CbrRateClient.Parse(XDocument.Parse(Soap(Row + Row.Replace("USD ", "EUR").Replace("840", "978"))), Requested).OfficialRate,
            Is.EqualTo(81.1234m));
    }

    [TestCase(503, "provider secret", typeof(HttpRequestException))]
    [TestCase(200, "not xml", typeof(XmlException))]
    [TestCase(200, "<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///private'>]><x>&a;</x>", typeof(XmlException))]
    [TestCase(200, "<Envelope/>", typeof(InvalidDataException))]
    public void RejectsHttpXmlAndSoapFailures(int status, string xml, Type error)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(xml) })));
        Assert.ThrowsAsync(error, () => new CbrRateClient(http, NullLogger<CbrRateClient>.Instance).GetAsync(Requested, default));
    }

    [Test]
    public async Task CancellationBeforeAndDuringRequestAndResponseSizeAreBounded()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage();
        }));
        var client = new CbrRateClient(http, NullLogger<CbrRateClient>.Instance);
        var pending = client.GetAsync(Requested, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await pending);
        Assert.CatchAsync<OperationCanceledException>(() => client.GetAsync(Requested, cancellation.Token));
        using var largeHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("<x>" + new string('x', 1_048_577) + "</x>") })));
        Assert.ThrowsAsync<XmlException>(() => new CbrRateClient(largeHttp, NullLogger<CbrRateClient>.Instance).GetAsync(Requested, default));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
