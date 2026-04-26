using PracticeX.Discovery.FieldExtraction;
using PracticeX.Discovery.Schemas;
using PracticeX.Discovery.TextExtraction;

namespace PracticeX.Tests.SourceDiscovery;

public class NdaExtractorTests
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
    public void CanExtract_NdaSubtypes()
    {
        var x = new NdaExtractor();
        Assert.True(x.CanExtract(NdaSchemaV1Constants.Subtypes.BilateralIndividual));
        Assert.True(x.CanExtract(NdaSchemaV1Constants.Subtypes.MutualOrg));
        Assert.True(x.CanExtract(NdaSchemaV1Constants.Subtypes.InvestorTemplate));
        Assert.True(x.CanExtract(NdaSchemaV1Constants.Subtypes.AdvisorTemplate));
        Assert.True(x.CanExtract(NdaSchemaV1Constants.Subtypes.DemoParticipantTemplate));
        Assert.True(x.CanExtract(NdaSchemaV1Constants.CandidateType));
        Assert.False(x.CanExtract("employee_agreement"));
        Assert.False(x.CanExtract("payer_contract"));
        Assert.False(x.CanExtract(""));
    }

    [Fact]
    public void Extract_BilateralIndividual_DetectsParties()
    {
        var body = """
            NON-DISCLOSURE AGREEMENT
            Effective Date: April 11, 2026
            This Agreement is entered into between Synexar, Inc. and Jane Doe, M.D.
            for the purpose of evaluating a potential clinical advisor engagement.
            This Agreement is governed by the laws of the State of Delaware.
            """;

        var x = new NdaExtractor();
        var result = x.Extract(Build(body, fileName: "Synexar_NDA_JaneDoe.pdf"));

        Assert.Equal(NdaSchemaV1Constants.Version, result.SchemaVersion);
        Assert.Equal(NdaSchemaV1Constants.Subtypes.BilateralIndividual, result.Subtype);
        Assert.Contains("subtype_detected:bilateral_individual", result.ReasonCodes);
        Assert.Contains("nda_extractor_v1", result.ReasonCodes);

        var parties = Assert.IsType<List<PartyRecord>>(result.Fields["parties"].Value);
        Assert.Equal(2, parties.Count);

        var isMutual = Assert.IsType<bool>(result.Fields["is_mutual"].Value);
        Assert.False(isMutual);
    }

    [Fact]
    public void Extract_MutualNda_FlagsMutual()
    {
        var body = """
            MUTUAL NON-DISCLOSURE AGREEMENT
            Effective Date: April 11, 2026
            This Mutual Non-Disclosure Agreement is entered into between Synexar, Inc. and Acme Corp.
            Each Party discloses Confidential Information to the other for the purpose of evaluating a potential commercial relationship.
            This Agreement is governed by the laws of the State of Delaware.
            """;

        var x = new NdaExtractor();
        var result = x.Extract(Build(body, fileName: "Mutual_NDA_Acme.pdf"));

        Assert.Equal(NdaSchemaV1Constants.Subtypes.MutualOrg, result.Subtype);
        var isMutual = Assert.IsType<bool>(result.Fields["is_mutual"].Value);
        Assert.True(isMutual);
    }

    [Fact]
    public void Extract_InvestorTemplate_FlagsTemplate()
    {
        var body = """
            INVESTOR NDA TEMPLATE
            Effective Date: ____________
            This Agreement is between Synexar, Inc. and [Counterparty Name].
            _____ Date: _____
            This Agreement is governed by the laws of the State of [Your State].
            """;

        var x = new NdaExtractor();
        var result = x.Extract(Build(body, fileName: "Investor_NDA_Template.docx"));

        Assert.Equal(NdaSchemaV1Constants.Subtypes.InvestorTemplate, result.Subtype);
        Assert.True(result.IsTemplate);
        Assert.False(result.IsExecuted);
        Assert.Contains("nda_template", result.ReasonCodes);
        Assert.DoesNotContain("extraction_partial", result.ReasonCodes);

        // Templates derive permitted_purpose from subtype.
        var purpose = Assert.IsType<string>(result.Fields["permitted_purpose"].Value);
        Assert.Contains("investment", purpose);

        // Templates seed parties[0] = Synexar.
        var parties = Assert.IsType<List<PartyRecord>>(result.Fields["parties"].Value);
        Assert.Single(parties);
        Assert.Equal("Synexar, Inc.", parties[0].Name);
    }

    [Fact]
    public void Extract_DocusignFromInput_AttachesSignature()
    {
        var body = """
            NON-DISCLOSURE AGREEMENT
            Effective Date: April 11, 2026
            This Agreement is between Synexar, Inc. and Jane Doe, M.D.
            for the purpose of evaluating a potential advisor engagement.
            This Agreement is governed by the laws of Delaware.
            """;

        var x = new NdaExtractor();
        var result = x.Extract(Build(
            body,
            fileName: "nda_signed.pdf",
            signatureProvider: "docusign",
            envelopeId: "11111111-2222-3333-4444-555555555555"));

        Assert.True(result.IsExecuted);
        Assert.Contains("signature_attached", result.ReasonCodes);
        var sigs = Assert.IsType<List<SignatureRecord>>(result.Fields["signature_block"].Value);
        var sig = Assert.Single(sigs);
        Assert.Equal("docusign", sig.SignatureProvider);
        Assert.Equal("11111111-2222-3333-4444-555555555555", sig.EnvelopeId);
    }

    [Fact]
    public void Extract_TermDefaults_TwoYearsForExecuted()
    {
        // Executed-looking body with no explicit term phrase.
        var executedBody = """
            NON-DISCLOSURE AGREEMENT
            Effective Date: April 11, 2026
            This Agreement is between Synexar, Inc. and Jane Doe, M.D.
            for the purpose of evaluating a potential advisor engagement.
            This Agreement is governed by the laws of Delaware.
            """;

        var x = new NdaExtractor();
        var executed = x.Extract(Build(
            executedBody,
            fileName: "nda_signed.pdf",
            signatureProvider: "docusign",
            envelopeId: "11111111-2222-3333-4444-555555555555"));

        var term = Assert.IsType<TermRecord>(executed.Fields["term"].Value);
        Assert.Equal("fixed_months", term.Type);
        Assert.Equal(24, term.Months);

        // Template body with no explicit term — term remains null.
        var templateBody = """
            INVESTOR NDA TEMPLATE
            Effective Date: ____________
            This Agreement is between Synexar, Inc. and [Counterparty Name].
            _____ Date: _____
            """;
        var template = x.Extract(Build(templateBody, fileName: "Investor_NDA_Template.docx"));
        Assert.True(template.IsTemplate);
        Assert.Null(template.Fields["term"].Value);
    }

    [Fact]
    public void Extract_NonSolicit_ExtractsMonths()
    {
        var body = """
            NON-DISCLOSURE AGREEMENT
            Effective Date: April 11, 2026
            This Agreement is between Synexar, Inc. and Jane Doe, M.D.
            for the purpose of evaluating a potential advisor engagement.
            The parties agree to a non-solicit period of 12 months following termination of this Agreement.
            This Agreement is governed by the laws of Delaware.
            """;

        var x = new NdaExtractor();
        var result = x.Extract(Build(body, fileName: "nda_with_nonsolicit.pdf"));

        var months = Assert.IsType<int>(result.Fields["non_solicit_term_months"].Value);
        Assert.Equal(12, months);
    }
}
