using PracticeX.Discovery.FieldExtraction;
using PracticeX.Discovery.Schemas;
using PracticeX.Discovery.TextExtraction;

namespace PracticeX.Tests.SourceDiscovery;

public class CorporateExtractorTests
{
    private static FieldExtractionInput Build(
        string body,
        string fileName = "doc.pdf",
        string? signatureProvider = null,
        string? envelopeId = null,
        IReadOnlyList<ExtractedHeading>? headings = null)
        => new()
        {
            FullText = body,
            Pages = new[] { new ExtractedPage(1, body) },
            Headings = headings ?? Array.Empty<ExtractedHeading>(),
            FileName = fileName,
            SignatureProvider = signatureProvider,
            DocusignEnvelopeId = envelopeId
        };

    [Fact]
    public void CanExtract_CorporateSubtypes()
    {
        var x = new CorporateExtractor();
        Assert.True(x.CanExtract(CorporateSchemaV1Constants.Subtypes.CertificateOfIncorporation));
        Assert.True(x.CanExtract(CorporateSchemaV1Constants.Subtypes.FilingReceipt));
        Assert.True(x.CanExtract(CorporateSchemaV1Constants.Subtypes.BoardConsent));
        Assert.True(x.CanExtract(CorporateSchemaV1Constants.Subtypes.FounderAgreement));
        Assert.True(x.CanExtract(CorporateSchemaV1Constants.Subtypes.FoundersCharter));
        Assert.True(x.CanExtract(CorporateSchemaV1Constants.Subtypes.StockPurchaseAgreement));
        Assert.True(x.CanExtract(CorporateSchemaV1Constants.Subtypes.Section83bElection));
        Assert.True(x.CanExtract(CorporateSchemaV1Constants.Subtypes.EinLetter));
        Assert.True(x.CanExtract(CorporateSchemaV1Constants.CandidateType));
        Assert.False(x.CanExtract("employee_agreement"));
        Assert.False(x.CanExtract("nda"));
        Assert.False(x.CanExtract(""));
    }

    [Fact]
    public void Extract_FilingReceipt_DetectsServiceRequestNumber()
    {
        var body = """
            SYNEXAR, INC.
            Order Summary
            Service Request Number 20254536349
            Date Submitted Wednesday, November 12, 2025 11:33:16 AM
            """;

        var x = new CorporateExtractor();
        var result = x.Extract(Build(body, fileName: "Synexar_FilingReceipt.pdf"));

        Assert.Equal(CorporateSchemaV1Constants.Version, result.SchemaVersion);
        Assert.Equal(CorporateSchemaV1Constants.Subtypes.FilingReceipt, result.Subtype);
        Assert.Equal("20254536349", result.Fields["service_request_number"].Value);
        Assert.Contains("corporate_extractor_v1", result.ReasonCodes);
        Assert.Contains("subtype_detected:filing_receipt", result.ReasonCodes);
    }

    [Fact]
    public void Extract_BoardConsent_ParsesResolutions()
    {
        var body = """
            UNANIMOUS WRITTEN CONSENT OF THE BOARD OF DIRECTORS
            Resolution Adoption
            WHEREAS the Company desires to adopt the 2025 Equity Incentive Plan, and
            Adoption of the Equity Incentive Plan
            RESOLVED that the Plan is adopted in full effect.
            Election of Officers
            RESOLVED FURTHER that the Board hereby elects officers as listed.
            """;

        var x = new CorporateExtractor();
        var result = x.Extract(Build(body, fileName: "Synexar_BoardConsent.pdf"));

        Assert.Equal(CorporateSchemaV1Constants.Subtypes.BoardConsent, result.Subtype);
        var resolutions = Assert.IsType<List<ResolutionRecord>>(result.Fields["resolutions"].Value);
        Assert.True(resolutions.Count >= 2,
            $"Expected at least 2 resolutions; got {resolutions.Count}");
    }

    [Fact]
    public void Extract_BoardConsent_ParsesShareAuthorization()
    {
        var body = """
            UNANIMOUS WRITTEN CONSENT OF THE BOARD OF DIRECTORS
            Resolution Adopted
            WHEREAS the Board has approved share reservation, and
            RESOLVED that 2,000,000 shares of Common Stock reserved for the Synexar 2025 Equity Incentive Plan.
            """;

        var x = new CorporateExtractor();
        var result = x.Extract(Build(body, fileName: "Synexar_BoardConsent.pdf"));

        Assert.Equal(CorporateSchemaV1Constants.Subtypes.BoardConsent, result.Subtype);
        var auths = Assert.IsType<List<ShareAuthorization>>(result.Fields["share_authorizations"].Value);
        Assert.NotEmpty(auths);
        var first = auths[0];
        Assert.Equal("Common", first.ShareClass);
        Assert.Equal(2_000_000L, first.Count);
        Assert.NotNull(first.PlanName);
        Assert.Contains("Plan", first.PlanName);
    }

    [Fact]
    public void Extract_FoundersCharter_FlagsNonBinding()
    {
        var body = """
            FOUNDERS CHARTER
            This document is non-binding and reflects the founders' shared intentions.
            Founder Name: Alice Founder
            Founder Name: Bob Founder
            8,000,000 shares authorized.
            6,000,000 shares issued.
            2,000,000 shares unissued.
            Guiding Principles
            - Build with integrity
            - Move fast, ship safely
            """;

        var x = new CorporateExtractor();
        var result = x.Extract(Build(body, fileName: "Synexar_FoundersCharter.pdf"));

        Assert.Equal(CorporateSchemaV1Constants.Subtypes.FoundersCharter, result.Subtype);
        Assert.Equal(false, result.Fields["binding"].Value);
        Assert.Contains("non_binding_document", result.ReasonCodes);
    }

    [Fact]
    public void Extract_FounderAgreement_DetectsPlaceholderTemplate()
    {
        var body = """
            FOUNDER AGREEMENT
            Effective Date: ____________
            This agreement is governed by the laws of [Your State].
            Founder Name: ____________
            """;

        var x = new CorporateExtractor();
        var result = x.Extract(Build(body, fileName: "Founder_Agreement_Template.docx"));

        Assert.Equal(CorporateSchemaV1Constants.Subtypes.FounderAgreement, result.Subtype);
        Assert.True(result.IsTemplate);
        Assert.Contains("template_placeholders_present", result.ReasonCodes);
    }

    [Fact]
    public void Extract_Form83b_ParsesTinAndProperty()
    {
        var body = """
            Form 15620 — Section 83(b) Election
            Taxpayer Name: Raghuram Vadapally
            TIN: 049-98-0888
            Property Description: 4,000,000 shares of Synexar, Inc.
            Date of transfer: 11/25/2025
            Tax Year: 2025
            Service Recipient: Synexar, Inc.
            """;

        var x = new CorporateExtractor();
        var result = x.Extract(Build(body, fileName: "Synexar_83b_Election.pdf"));

        Assert.Equal(CorporateSchemaV1Constants.Subtypes.Section83bElection, result.Subtype);
        Assert.Equal("049-98-0888", result.Fields["taxpayer_tin"].Value);
        var prop = result.Fields["property_description"].Value as string;
        Assert.NotNull(prop);
        Assert.Contains("4,000,000", prop);
        Assert.Contains("Synexar", prop);
    }

    [Fact]
    public void Extract_Form83b_ParsesTimestampSignature()
    {
        var body = """
            Form 15620 — Section 83(b) Election
            Taxpayer Name: Raghuram Vadapally
            Property Description: 4,000,000 shares of Synexar, Inc.

            RAGHURAM VADAPALLY 2025.11.26 02:43:14 +0000
            """;

        var x = new CorporateExtractor();
        var result = x.Extract(Build(body, fileName: "Synexar_83b_Election.pdf"));

        Assert.Equal(CorporateSchemaV1Constants.Subtypes.Section83bElection, result.Subtype);
        var signedAt = Assert.IsType<DateTimeOffset>(result.Fields["signed_at_utc"].Value);
        Assert.Equal(2025, signedAt.Year);
        Assert.Equal(11, signedAt.Month);
        Assert.Equal(26, signedAt.Day);
        Assert.Equal(2, signedAt.Hour);
        Assert.Equal(43, signedAt.Minute);
        Assert.Equal(14, signedAt.Second);
        Assert.Equal(TimeSpan.Zero, signedAt.Offset);
    }

    [Fact]
    public void Extract_EinLetter_ExtractsEin()
    {
        var body = """
            SYNEXAR, INC.
            Department of the Treasury
            Internal Revenue Service
            We have issued your Employer Identification Number.
            EIN: 41-2773035
            Date: November 1, 2025
            """;

        var x = new CorporateExtractor();
        var result = x.Extract(Build(body, fileName: "Synexar_EIN_Letter.pdf"));

        Assert.Equal(CorporateSchemaV1Constants.Subtypes.EinLetter, result.Subtype);
        Assert.Equal("41-2773035", result.Fields["ein"].Value);
    }
}
