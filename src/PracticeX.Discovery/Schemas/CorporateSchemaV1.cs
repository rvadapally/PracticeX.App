namespace PracticeX.Discovery.Schemas;

/// <summary>
/// Wire-shape constants and records for the Corporate / Foundation v1
/// contract family (certificate of incorporation, filing receipt, board
/// consent, founder agreement, founders charter, stock purchase agreement,
/// Section 83(b) election, EIN letter). Mirrors
/// docs/contract-schemas/corporate_v1.md. Reuses PartyRecord, AddressRecord,
/// MoneyRecord, EquityGrant, VestingTerms, ProRataIncrement, SignatureRecord
/// from the shared <c>PracticeX.Discovery.Schemas</c> namespace — those are
/// not redefined here.
/// </summary>
public static class CorporateSchemaV1Constants
{
    public const string Version = "corporate_v1";
    public const string CandidateType = "corporate";

    public static class Subtypes
    {
        public const string CertificateOfIncorporation = "certificate_of_incorporation";
        public const string FilingReceipt = "filing_receipt";
        public const string BoardConsent = "board_consent";
        public const string FounderAgreement = "founder_agreement";
        public const string FoundersCharter = "founders_charter";
        public const string StockPurchaseAgreement = "stock_purchase_agreement";
        public const string Section83bElection = "section_83b_election";
        public const string EinLetter = "ein_letter";

        public static readonly IReadOnlyList<string> All =
            [CertificateOfIncorporation, FilingReceipt, BoardConsent, FounderAgreement,
             FoundersCharter, StockPurchaseAgreement, Section83bElection, EinLetter];
    }
}

// Corporate-specific sub-schemas (not reused outside this family)
public sealed record ShareAuthorization(
    string ShareClass,
    long Count,
    string? PlanName,
    string? ReservedFor
);

public sealed record ResolutionRecord(
    int? SequenceNumber,
    string Title,
    string BodyText,
    string Type    // "equity_plan_adoption" | "officer_election" | "share_issuance" | "amendment" | "other"
);

public sealed record FounderRecord(
    PartyRecord Party,
    string Role,
    string? Title,
    decimal? EquityPercent,
    IReadOnlyList<string> Responsibilities
);

public sealed record EquityAllocation(
    long? TotalAuthorized,
    long? TotalIssued,
    long? TotalUnissued,
    long? PerFounderShareCount
);

public sealed record GovernanceRules(
    MoneyRecord? MajorDecisionThreshold,
    IReadOnlyDictionary<string, string>? DecisionAuthority,
    string? DeadlockResolution
);
