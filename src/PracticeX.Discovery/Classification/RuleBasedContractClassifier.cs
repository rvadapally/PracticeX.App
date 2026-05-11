using System.Text.RegularExpressions;
using PracticeX.Domain.Documents;

namespace PracticeX.Discovery.Classification;

/// <summary>
/// Deterministic, rule-based classifier. Production replaces this with the LLM
/// extraction pipeline; the rule version stays as the lightweight pre-pass that
/// hydrates the discovery UI immediately and emits explainable reason codes.
///
/// Slice 8 expanded the type taxonomy and switched to regex word-boundary
/// matching so short codes ("nda", "loi", "lease") don't false-match inside
/// longer words ("addendum", "loaded", "release").
/// </summary>
public sealed class RuleBasedContractClassifier : IDocumentClassifier
{
    public string Version => "rule_v2";

    // ----------------------------------------------------------------------
    // Type-specific patterns. Order in InferType is precedence (most specific
    // first); the first match wins. Compile once for speed.
    // ----------------------------------------------------------------------
    private static readonly RegexOptions RxOpts =
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Bylaws — corporate governance. "Bylaws" is unique enough to substring.
    private static readonly Regex BylawsRx = new(@"by[-_\s]?laws?|articles?\s+of\s+incorporation|operating\s+agreement", RxOpts);

    // Call coverage. Handles smushed CamelCase ("EagleGICallcoverage") because
    // the term is unique — won't false-match anything common.
    private static readonly Regex CallCoverageRx = new(@"call[-_\s]?coverage|callcoverage|cross[-_\s]?coverage", RxOpts);

    // NDA / confidentiality. "nda" needs word-boundary to avoid matching inside
    // longer words; "non-disclosure" and "confidentiality agreement" are unique.
    private static readonly Regex MutualNdaRx = new(@"mutual[-_\s]+(non[-_\s]?disclosure|nda)", RxOpts);
    private static readonly Regex NdaRx = new(@"non[-_\s]?disclosure|confidentiality\s+agreement|\bnda\b", RxOpts);

    // Letter-of-intent for real estate. "loi" is a short token — bound it.
    private static readonly Regex LeaseLoiRx = new(@"\bloi\b.*\b(lease|renewal|tenant|premises|sublease|space|sqft|sq\.?\s*ft)\b|\b(lease|renewal|tenant|premises)\b.*\bloi\b", RxOpts);
    private static readonly Regex RenewalLoiRx = new(@"\bloi[-_\s].*renewal|renewal[-_\s].*\bloi\b", RxOpts);

    // Lease amendment — captures Eagle GI's "Amemdment" typo too. Uses
    // word-boundary on "lease" to avoid matching "release".
    private static readonly Regex LeaseAmendmentRx = new(@"\blease(s|d)?\b.*(amendment|amemdment|addendum|rider|modification)|(amendment|amemdment|addendum|rider).*\blease(s|d)?\b", RxOpts);

    // Generic lease — word-boundary on "lease" so "release" doesn't match.
    // Other terms (sublease, tenant, etc.) are unique enough without boundary.
    private static readonly Regex LeaseRx = new(@"\blease(s|d)?\b|sublease|tenant|landlord|premises|suite\s+\d|sqft|sq\.?\s*ft|rent\s+schedule", RxOpts);

    // Service agreement.
    private static readonly Regex ServiceAgreementRx = new(@"service\s+agreement|master\s+services?\s+agreement|\bmsa\b", RxOpts);

    // Employment / physician.
    private static readonly Regex EmploymentRx = new(@"employment|physician|comp\s+addendum|compensation|noncompete|engagement\s+letter|\badvisor\b|shareholder\s+physician", RxOpts);

    // Payer (insurance). "uhc" needs word boundary; full names are unique.
    private static readonly Regex PayerRx = new(@"\bpayer\b|blue\s?shield|regence|\baetna\b|\bcigna\b|united[-_\s]?healthcare|\buhc\b|humana|anthem|\bkaiser\b|medicare\s+advantage", RxOpts);

    // Processor / BAA.
    private static readonly Regex ProcessorRx = new(@"\bbaa\b|business\s+associate|processor\s+agreement|data\s+processing|hipaa", RxOpts);

    // Vendor (broad — last in the chain).
    private static readonly Regex VendorRx = new(@"\bvendor\b|\bsow\b|olympus|stryker|boston\s+scientific|supplies|\bequipment\b|labs?\s+service", RxOpts);

    // Generic amendment (when nothing more specific matched).
    private static readonly Regex AmendmentGenericRx = new(@"\b(amendment|amemdment|addendum|rider)\b", RxOpts);

    // Generic contract / agreement keyword (small confidence boost).
    private static readonly Regex ContractKeywordRx = new(@"\b(contract|agreement)\b", RxOpts);

    // Fee / rate schedule.
    private static readonly Regex FeeScheduleRx = new(@"fee\s+schedule|rate\s+schedule|exhibit\s+[ab]\b|rate\s+sheet", RxOpts);

    private static readonly string[] SupportedMimeTypes =
    [
        "application/pdf",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "text/plain",
        "image/tiff",
        "image/png",
        "image/jpeg"
    ];

    public ClassificationResult Classify(ClassificationInput input)
    {
        var reasons = new List<string>();

        if (!IsSupportedMimeType(input.MimeType, input.FileName))
        {
            reasons.Add(IngestionReasonCodes.UnsupportedMimeType);
            return new ClassificationResult
            {
                CandidateType = DocumentCandidateTypes.Other,
                Confidence = 0.0m,
                ReasonCodes = reasons,
                Status = DocumentCandidateStatus.Skipped
            };
        }

        if (input.SizeBytes is 0)
        {
            reasons.Add(IngestionReasonCodes.EmptyFile);
            return new ClassificationResult
            {
                CandidateType = DocumentCandidateTypes.Unknown,
                Confidence = 0.0m,
                ReasonCodes = reasons,
                Status = DocumentCandidateStatus.Skipped
            };
        }

        // Normalize underscores to spaces so word-boundary regexes work on
        // filename-safe names like "04_eec_lease_4th_amend.pdf" the same way
        // they do on human-typed "04 EEC Lease 4th Amendment.pdf".
        var haystack = string.Join(" ",
            input.FileName,
            input.RelativePath ?? string.Empty,
            input.SubjectHint ?? string.Empty,
            input.SenderHint ?? string.Empty,
            input.FolderHint ?? string.Empty)
            .Replace('_', ' ');

        var (type, baseConfidence, typeReasons) = InferType(haystack);
        reasons.AddRange(typeReasons);

        var confidence = baseConfidence;

        if (ContractKeywordRx.IsMatch(haystack))
        {
            reasons.Add(IngestionReasonCodes.FilenameContractKeywords);
            confidence = Math.Min(0.99m, confidence + 0.05m);
        }

        // If the type isn't already "amendment-like" and the doc has amendment
        // markers, demote to plain Amendment unless we already classified more
        // specifically (e.g. LeaseAmendment).
        if (AmendmentGenericRx.IsMatch(haystack) &&
            type != DocumentCandidateTypes.LeaseAmendment &&
            type != DocumentCandidateTypes.Amendment)
        {
            reasons.Add(IngestionReasonCodes.FilenameAmendment);
            confidence = Math.Min(0.99m, confidence + 0.04m);
            if (type == DocumentCandidateTypes.Unknown)
            {
                type = DocumentCandidateTypes.Amendment;
                confidence = Math.Max(confidence, 0.6m);
            }
        }

        if (FeeScheduleRx.IsMatch(haystack))
        {
            reasons.Add(IngestionReasonCodes.FilenameRateSchedule);
            confidence = Math.Min(0.99m, confidence + 0.04m);
            if (type == DocumentCandidateTypes.Unknown)
            {
                type = DocumentCandidateTypes.FeeSchedule;
                confidence = Math.Max(confidence, 0.6m);
            }
        }

        if (input.FolderHint?.Contains("payer", StringComparison.OrdinalIgnoreCase) == true &&
            type == DocumentCandidateTypes.PayerContract)
        {
            reasons.Add(IngestionReasonCodes.FolderHintPayer);
            confidence = Math.Min(0.99m, confidence + 0.03m);
        }
        if (input.FolderHint?.Contains("lease", StringComparison.OrdinalIgnoreCase) == true)
        {
            reasons.Add(IngestionReasonCodes.FolderHintLease);
            if (type == DocumentCandidateTypes.Unknown)
            {
                type = DocumentCandidateTypes.Lease;
                confidence = Math.Max(confidence, 0.6m);
            }
        }

        foreach (var hint in input.Hints)
        {
            reasons.Add(hint);
        }

        if (type == DocumentCandidateTypes.Unknown)
        {
            reasons.Add(IngestionReasonCodes.AmbiguousType);
        }
        else
        {
            reasons.Add(IngestionReasonCodes.LikelyContract);
        }

        var status = confidence >= 0.55m
            ? DocumentCandidateStatus.PendingReview
            : DocumentCandidateStatus.Candidate;

        var counterparty = ExtractCounterpartyHint(haystack);

        return new ClassificationResult
        {
            CandidateType = type,
            Confidence = decimal.Round(confidence, 4),
            ReasonCodes = reasons,
            Status = status,
            CounterpartyHint = counterparty
        };
    }

    /// <summary>
    /// Priority-ordered type inference. First match wins. More specific
    /// patterns come first so a "Mutual NDA on Lease Premises" matches
    /// MutualNda, not Lease.
    /// </summary>
    private static (string type, decimal confidence, IEnumerable<string> reasons) InferType(string haystack)
    {
        var reasons = new List<string>();

        // 1. Bylaws / corporate governance.
        if (BylawsRx.IsMatch(haystack))
        {
            reasons.Add(IngestionReasonCodes.FilenameBylaws);
            return (DocumentCandidateTypes.Bylaws, 0.78m, reasons);
        }

        // 2. Call coverage (physician scheduling) — must come before Employment
        // because "physician" might appear in a call coverage contract too.
        if (CallCoverageRx.IsMatch(haystack))
        {
            reasons.Add(IngestionReasonCodes.FilenameCallCoverage);
            return (DocumentCandidateTypes.CallCoverageAgreement, 0.78m, reasons);
        }

        // 3. NDA — Mutual variant gets priority over generic NDA.
        if (MutualNdaRx.IsMatch(haystack))
        {
            reasons.Add(IngestionReasonCodes.FilenameMutualNda);
            reasons.Add(IngestionReasonCodes.FilenameNda);
            return (DocumentCandidateTypes.Nda, 0.82m, reasons);
        }
        if (NdaRx.IsMatch(haystack))
        {
            reasons.Add(IngestionReasonCodes.FilenameNda);
            return (DocumentCandidateTypes.Nda, 0.78m, reasons);
        }

        // 4. Lease LOI — must precede LeaseAmendment because LOI text doesn't
        // contain "amendment" but does contain "lease/renewal".
        if (LeaseLoiRx.IsMatch(haystack) || RenewalLoiRx.IsMatch(haystack))
        {
            reasons.Add(IngestionReasonCodes.FilenameLeaseLoi);
            reasons.Add(IngestionReasonCodes.FilenameRenewalLoi);
            return (DocumentCandidateTypes.LeaseLoi, 0.80m, reasons);
        }

        // 5. Lease amendment — more specific than plain lease.
        if (LeaseAmendmentRx.IsMatch(haystack))
        {
            reasons.Add(IngestionReasonCodes.FilenameLeaseAmendment);
            return (DocumentCandidateTypes.LeaseAmendment, 0.80m, reasons);
        }

        // 6. Generic lease (premises/tenant/landlord/etc).
        if (LeaseRx.IsMatch(haystack))
        {
            return (DocumentCandidateTypes.Lease, 0.74m, reasons);
        }

        // 7. Service agreement — more specific than vendor.
        if (ServiceAgreementRx.IsMatch(haystack))
        {
            reasons.Add(IngestionReasonCodes.FilenameServiceAgreement);
            return (DocumentCandidateTypes.ServiceAgreement, 0.74m, reasons);
        }

        // 8. Employment.
        if (EmploymentRx.IsMatch(haystack))
        {
            return (DocumentCandidateTypes.EmployeeAgreement, 0.7m, reasons);
        }

        // 9. Processor agreement (BAA / HIPAA).
        if (ProcessorRx.IsMatch(haystack))
        {
            return (DocumentCandidateTypes.ProcessorAgreement, 0.7m, reasons);
        }

        // 10. Payer (insurance) — broad; comes after lease so /payer/lease/ paths
        // resolve to lease (more specific) first.
        if (PayerRx.IsMatch(haystack))
        {
            return (DocumentCandidateTypes.PayerContract, 0.78m, reasons);
        }

        // 11. Vendor (broadest commercial bucket).
        if (VendorRx.IsMatch(haystack))
        {
            return (DocumentCandidateTypes.VendorContract, 0.7m, reasons);
        }

        return (DocumentCandidateTypes.Unknown, 0.4m, reasons);
    }

    private static bool IsSupportedMimeType(string mimeType, string fileName)
    {
        if (SupportedMimeTypes.Contains(mimeType, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".txt" or ".tif" or ".tiff" or ".png" or ".jpg" or ".jpeg";
    }

    private static string? ExtractCounterpartyHint(string haystack)
    {
        // Surface the matched payer/vendor keyword (if any) as a counterparty hint.
        var lowered = haystack.ToLowerInvariant();
        string[] payerHints = ["aetna", "cigna", "humana", "anthem", "kaiser", "blue shield", "blueshield", "regence", "uhc", "united healthcare"];
        string[] vendorHints = ["olympus", "stryker", "boston scientific"];
        foreach (var kw in payerHints.Concat(vendorHints))
        {
            if (lowered.Contains(kw))
            {
                return kw;
            }
        }
        return null;
    }
}
