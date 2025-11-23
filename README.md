# DentalPay
DentalPay - ASP.NET Core billing &amp; invoicing system for a multi-chair dental clinic (insurance coverage, reporting, XML/TXT/XLS exports).


**DentalPay** is a production ASP.NET Core MVC application used in a multi-chair dental clinic for:

- creating invoices for dental services  
- applying health insurance coverage rules (fund vs patient)  
- exporting reports for the local health fund (XML / TXT / XLS)

> ⚠️ This repository contains **selected code excerpts** from the real system  
> (no sensitive data, no full DB schema).  
> The goal is to showcase backend logic and integrations, not to provide a fully runnable product.

---

## Project context (what this app does)

- Used daily in a **dental clinic** as an internal line-of-business app  
- Handles **hundreds of invoices** with:
  - different invoice types (5 and 14)  
  - insurance coverage codes  
  - patient co-payment (participation)  
- Generates machine-readable exports for the **health fund** (XML / TXT / XLS)  
- Built and maintained as a **single-developer** backend project (me 🙂)

---

## Tech stack

- **Backend:** ASP.NET Core MVC, C#
- **Data access:** Entity Framework Core, async/await, LINQ
- **Database:** SQL Server
- **Exports & integration:**
  - System.Xml.Linq (XML generation)
  - NPOI (Excel .xls export)
  - Custom fixed-width TXT format
- **Other:** Authentication/Authorization (`[Authorize]`), ViewModels, DTOs

---

## Invoices: domain & flow

The core of this system is invoice management:

- **Invoice types:** 5 and 14  
  - Each type has different required fields  
  - Type 5 requires account number & handover date  
  - Type 14 uses different coverage rules  
- **Patient resolution:**
  - Patient is resolved by **JMBG** (national ID)  
  - If no patient exists, a new one is created using JMBG and name  
- **Coverage:**
  - Coverage is mapped by `CoverageCode` → `CoverageType` entity  
  - Service pricing (fund vs patient) is calculated via `ServiceCoverageRules`
- **Validation:**
  - ModelState validation + domain rules (required fields per type)  
  - Uniqueness of `IdUputnice` (referral ID) for invoices  
  - All key references (doctor, diagnosis, facility, referring doctor) are verified in DB  

In this repository you can see this logic in:

- `InvoicesController` → `Create(InvoiceCreateViewModel model)`

That method shows:

- how input is validated  
- how a patient is resolved / created  
- how coverage and services are used to calculate totals for fund & patient  
- how `Invoice` and related `InvoiceItem` entities are created and saved

---

## XML export – how it’s implemented (overview)

The health fund requires a **strict XML format** with:

- a specific root element and namespace  
- `xsi:schemaLocation` pointing to the official XSD  
- attributes for:
  - invoice number, date, amount, currency  
  - patient JMBG and name  
  - diagnosis code  
  - referring facility and doctor  
  - per-item data (service code split into parts, quantities, amounts, participation flags)

In code, this is implemented in two main steps:

1. **Query and aggregate data from EF Core**

   - Filter invoices by:
     - date range (`DateOd`, `DateDo`)  
     - invoice type (5 or 14)  
     - locked invoices only (`IsLocked = true`)  
     - invoices that belong to an aggregated invoice (`ZbirnaFakturaBroj` not null)
   - Include related entities:
     - `InvoiceItems` + `Service`
     - `Patient`
     - `Diagnosis`
     - `ReferringFacility`
     - `ReferringDoctor`
   - Calculate totals for fund and patient participation per invoice.

2. **Map domain model → XML using `XDocument` / `XElement`**

   - Define namespaces (`xmlns`, `xsi`) and `schemaLocation`
   - Create root `<fakture-zdravstva>` and `<opsti-podaci>` with static metadata
   - For each invoice:
     - create `<faktura>` element with attributes
     - then for each invoice item:
       - create `<stavka>` element with service code, quantity and amounts
   - Use `CultureInfo.InvariantCulture` to format decimal values (`"F2"`).

### Minimal example (stripped down to the idea)

Below is a **simplified** example showing the approach (not the full production format):

```csharp
// 1) Query invoices we want to export
var invoices = await _context.Invoices
    .Include(i => i.InvoiceItems).ThenInclude(ii => ii.Service)
    .Include(i => i.Patient)
    .Where(i => i.IsLocked)
    .Where(i => i.InvoiceDate >= model.DateOd && i.InvoiceDate <= model.DateDo)
    .ToListAsync();

// 2) Define XML namespaces
XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
XNamespace ns  = "http://example.org/health/invoices";

// 3) Build XML document
var xdoc = new XDocument(
    new XDeclaration("1.0", "utf-8", null),
    new XElement(ns + "fakture-zdravstva",
        new XAttribute("xmlns", ns),
        new XAttribute(XNamespace.Xmlns + "xsi", xsi),
        new XAttribute(
            xsi + "schemaLocation",
            "http://example.org/health/invoices http://example.org/health/invoices.xsd"
        ),
        new XElement(ns + "opsti-podaci",
            new XAttribute("ustanova-sifra", "11111"),
            new XAttribute("vrsta-fakture", model.TipFakture)
        ),
        invoices.Select(inv =>
        {
            decimal fondSum = inv.InvoiceItems.Sum(ii => ii.FundAmount);
            decimal pacSum  = inv.InvoiceItems.Sum(ii => ii.PatientAmount);

            var faktura = new XElement(ns + "faktura",
                new XAttribute("broj", inv.ZbirnaFakturaBroj ?? ""),
                new XAttribute("datum", inv.InvoiceDate.ToString("yyyy-MM-dd")),
                new XAttribute("iznos",
                    fondSum.ToString("F2", CultureInfo.InvariantCulture)),
                new XAttribute("participacija-iznos",
                    pacSum.ToString("F2", CultureInfo.InvariantCulture)),
                new XAttribute("osiguranik-jmb", inv.Patient?.Jmbg ?? "")
            );

            int rb = 1;
            foreach (var ii in inv.InvoiceItems)
            {
                faktura.Add(
                    new XElement(ns + "stavka",
                        new XAttribute("redni-broj", rb++),
                        new XAttribute("sifra-usluge", ii.Service?.ServiceCode ?? ""),
                        new XAttribute("kolicina", ii.Quantity),
                        new XAttribute("iznos",
                            ii.FundAmount.ToString("F2", CultureInfo.InvariantCulture))
                    )
                );
            }

            return faktura;
        })
    )
);

