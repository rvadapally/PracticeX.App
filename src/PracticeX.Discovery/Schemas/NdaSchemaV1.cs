namespace PracticeX.Discovery.Schemas;

/// <summary>
/// Wire-shape constants for the NDA v1 contract family. Mirrors
/// docs/contract-schemas/nda_v1.md. NDA reuses PartyRecord, TermRecord,
/// SignatureRecord, AddressRecord from EmploymentSchemaV1 — those records
/// live in the shared <c>PracticeX.Discovery.Schemas</c> namespace and are
/// not redefined here.
/// </summary>
public static class NdaSchemaV1Constants
{
    public const string Version = "nda_v1";

    /// <summary>Classifier candidate type that maps to this family.</summary>
    public const string CandidateType = "nda";

    public static class Subtypes
    {
        public const string BilateralIndividual = "bilateral_individual";
        public const string MutualOrg = "mutual_org";
        public const string InvestorTemplate = "investor_template";
        public const string AdvisorTemplate = "advisor_template";
        public const string DemoParticipantTemplate = "demo_participant_template";

        public static readonly IReadOnlyList<string> All =
            [BilateralIndividual, MutualOrg, InvestorTemplate, AdvisorTemplate, DemoParticipantTemplate];

        public static readonly IReadOnlyList<string> Templates =
            [InvestorTemplate, AdvisorTemplate, DemoParticipantTemplate];
    }
}
