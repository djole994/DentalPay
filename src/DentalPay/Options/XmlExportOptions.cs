namespace DentalPay.Options;

public sealed class XmlExportOptions
{
    public const string SectionName = "XmlExportOptions";

    public string FacilityCode { get; init; } = "XXXXX";
    public string FacilityName { get; init; } = "Naziv ustanove";
    public string NamespaceUri { get; init; } = "urn:example:health";
    public string SchemaUri { get; init; } = "https://example.org/schema.xsd";
    public string XslHref { get; init; } = "https://example.org/style.xsl";
    public string Currency { get; init; } = "BAM";
    public string XmlVersion { get; init; } = "1";
}
