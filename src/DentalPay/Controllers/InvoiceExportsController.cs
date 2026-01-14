using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DentalPay.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

[Route("exports/invoices")]
public class InvoiceExportsController : Controller
{
    private readonly StomatologijaContext _context;
    private readonly XmlExportOptions 

    public InvoiceExportsController(StomatologijaContext context, IOptions<XmlExportOptions> xmlOptions)
    {
        _context = context;
        _xml = xmlOptions.Value;
    }

    #region Endpoints

    [HttpGet("xml")]
    public IActionResult Xml()
    {
        var today = DateTime.Today;
        var firstDayOfMonth = new DateTime(today.Year, today.Month, 1);
        var lastDayOfMonth = firstDayOfMonth.AddMonths(1).AddDays(-1);

        var vm = new DownloadXmlFormViewModel
        {
            DateOd = firstDayOfMonth,
            DateDo = lastDayOfMonth
        };

        return View("Xml", vm);
    }

    [HttpPost("xml")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Xml(DownloadXmlFormViewModel model)
    {
        var error = ValidateXmlRequest(model);
        if (error is not null)
        {
            model.ErrorMessage = error;
            return View("Xml", model);
        }

        var dateOd = model.DateOd!.Value;
        var dateDo = model.DateDo!.Value;
        var tip = model.TipFakture!.Value;

        var invoices = await BuildInvoicesQuery(dateOd, dateDo, tip)
            .OrderBy(i => i.ZbirnaFakturaBroj)
            .ToListAsync();

        if (invoices.Count == 0)
        {
            model.ErrorMessage = "Nema faktura u zadatom intervalu.";
            return View("Xml", model);
        }

        var xdoc = BuildXmlDocument(invoices, dateOd, dateDo, tip);
        var fileName = BuildFileName(dateDo, tip);

        return XmlFileResult(xdoc, fileName);
    }

    #endregion

    #region Validation

    private static string? ValidateXmlRequest(DownloadXmlFormViewModel model)
    {
        if (!model.DateOd.HasValue || !model.DateDo.HasValue)
            return "Morate uneti oba datuma (Od i Do).";

        if (model.DateOd.Value >= model.DateDo.Value)
            return "Datum 'Od' mora biti manji od datuma 'Do'.";

        if (!model.TipFakture.HasValue || (model.TipFakture != 5 && model.TipFakture != 14))
            return "Tip fakture mora biti 5 ili 14.";

        return null;
    }

    #endregion

    #region Data access

    private IQueryable<Invoice> BuildInvoicesQuery(DateTime dateOd, DateTime dateDo, int tipFakture)
    {
        return _context.Invoices
            .AsNoTracking()
            .Include(i => i.InvoiceItems).ThenInclude(ii => ii.Service)
            .Include(i => i.Patient)
            .Include(i => i.Diagnosis)
            .Include(i => i.ReferringFacility)
            .Include(i => i.ReferringDoctor)
            .Where(i => i.IsLocked)
            .Where(i => i.InvoiceTip == tipFakture)
            .Where(i => i.InvoiceDate >= dateOd && i.InvoiceDate <= dateDo)
            .Where(i => i.ZbirnaFakturaBroj != null);
    }

    #endregion

    #region XML building

    private XDocument BuildXmlDocument(List<Invoice> invoices, DateTime dateOd, DateTime dateDo, int tipFakture)
    {
        XNamespace ns = _xml.NamespaceUri;
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XProcessingInstruction("xml-stylesheet", $"type=\"text/xsl\" href=\"{_xml.XslHref}\""),
            new XElement(ns + "fakture-zdravstva",
                new XAttribute("xmlns", ns.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsi", xsi),
                new XAttribute(xsi + "schemaLocation", $"{_xml.NamespaceUri} {_xml.SchemaUri}"),

                new XElement(ns + "opsti-podaci",
                    new XAttribute("ustanova-sifra", _xml.FacilityCode),
                    new XAttribute("ustanova-naziv", _xml.FacilityName),
                    new XAttribute("vrsta-fakture", tipFakture),
                    new XAttribute("jif", DateTime.Now.ToString("yyyyMMddHHmmssffff")),
                    new XAttribute("xml-verzija", _xml.XmlVersion)
                ),

                invoices.Select(inv => CreateFakturaElement(ns, inv, dateOd, dateDo, tipFakture))
            )
        );
    }

    private XElement CreateFakturaElement(XNamespace ns, Invoice inv, DateTime dateOd, DateTime dateDo, int tipFakture)
    {
        var fondSum = inv.InvoiceItems.Sum(ii => ii.FundAmount);
        var pacSum = inv.InvoiceItems.Sum(ii => ii.PatientAmount);

        var zbirnaDatum = inv.ZbirnaFakturaDate ?? inv.InvoiceDate;
        var rokPlacanja = zbirnaDatum.AddMonths(1);

        var jmb = inv.Patient?.Jmbg ?? "0000000000000";
        var imePrezime = inv.Patient is null ? "" : $"{inv.Patient.FirstName} {inv.Patient.LastName}";

        var refFacCode = inv.ReferringFacility?.Code ?? "";
        var refFacName = inv.ReferringFacility?.Name ?? "";

        var docCode = inv.ReferringDoctor?.CodeDoctor ?? "";
        var docFull = inv.ReferringDoctor is null ? "" : $"{inv.ReferringDoctor.FirstName} {inv.ReferringDoctor.LastName}";

        var diag = inv.Diagnosis?.DiagnosisCode ?? "";

        var xFaktura = new XElement(ns + "faktura",
            new XAttribute("tip", "R"),
            new XAttribute("broj", StripYearPrefix(inv.ZbirnaFakturaBroj ?? "")),
            new XAttribute("datum", zbirnaDatum.ToString("yyyy-MM-dd")),
            new XAttribute("rok-placanja", rokPlacanja.ToString("yyyy-MM-dd")),
            new XAttribute("valuta", _xml.Currency),
            new XAttribute("iznos", ToMoney(fondSum)),
            new XAttribute("participacija-placa", pacSum > 0 ? "D" : "N"),
            new XAttribute("participacija-iznos", ToMoney(pacSum)),

            new XAttribute("osiguranik-jmb", jmb),
            new XAttribute("osiguranik-ime-prezime", imePrezime),

            // Ako želiš da period bude dateOd/dateDo umjesto InvoiceDate, promijeni ovdje:
            new XAttribute("lijecenje-period-od", inv.InvoiceDate.ToString("yyyy-MM-dd")),
            new XAttribute("lijecenje-period-do", inv.InvoiceDate.ToString("yyyy-MM-dd")),

            new XAttribute("dijagnoza-oznaka", diag),
            new XAttribute("ustanova-uputilac-sifra", refFacCode),
            new XAttribute("ustanova-uputilac-naziv", refFacName),
            new XAttribute("ljekar-oznaka", docCode),
            new XAttribute("ljekar-ime-prezime", docFull),

            tipFakture == 5 ? new XAttribute("rjesenje-broj", inv.Account_number ?? "") : null,
            tipFakture == 14 ? new XAttribute("uputnica-id", inv.IdUputnice ?? "") : null
        );

        AddStavke(ns, xFaktura, inv, tipFakture);

        return xFaktura;
    }

    private static void AddStavke(XNamespace ns, XElement xFaktura, Invoice inv, int tipFakture)
    {
        var rb = 1;

        foreach (var ii in inv.InvoiceItems)
        {
            var (nivo, grupa, oznaka) = ParseServiceCode(ii.Service?.ServiceCode);

            var stavkaFond = ii.FundAmount;
            var stavkaPac = ii.PatientAmount;

            var xStavka = new XElement(ns + "stavka",
                new XAttribute("redni-broj", rb++),
                new XAttribute("usluga-nivo", nivo),
                new XAttribute("usluga-grupa", grupa),
                new XAttribute("usluga-oznaka", oznaka),
                new XAttribute("kolicina", ii.Quantity),
                new XAttribute("iznos", ToMoney(stavkaFond)),
                new XAttribute("participacija-placa", stavkaPac > 0 ? "D" : "N"),
                new XAttribute("participacija-iznos", ToMoney(stavkaPac)),
                tipFakture == 5
                    ? new XAttribute("datum-preuzimanja", inv.Handover?.ToString("yyyy-MM-dd") ?? "")
                    : null
            );

            xFaktura.Add(xStavka);
        }
    }

    #endregion

    #region File response

    private string BuildFileName(DateTime dateDo, int tipFakture)
        => $"{_xml.FacilityCode}_{dateDo:ddMMyyyy}_{tipFakture:D2}.xml";

    private FileContentResult XmlFileResult(XDocument xdoc, string fileName)
    {
        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            xdoc.Save(writer);
            writer.Flush();
        }

        return File(ms.ToArray(), "application/xml", fileName);
    }

    #endregion

    #region Helpers

    private static string ToMoney(decimal value)
        => value.ToString("F2", CultureInfo.InvariantCulture);

    private static (string nivo, string grupa, string oznaka) ParseServiceCode(string? raw)
    {
        var code = raw ?? "";

        var nivo = code.Length >= 1 ? code.Substring(0, 1) : "0";

        var grupa = code.Length >= 4
            ? code.Substring(1, 3)
            : code.PadRight(4, '0').Substring(1, 3);

        var oznaka = code.Length >= 7
            ? code.Substring(4, 3)
            : code.PadLeft(7, '0').Substring(4, 3);

        return (nivo, grupa, oznaka);
    }

    private static string StripYearPrefix(string value) => value;

    #endregion
}
