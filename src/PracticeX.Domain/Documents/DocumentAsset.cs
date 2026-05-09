using PracticeX.Domain.Common;

namespace PracticeX.Domain.Documents;

public sealed class DocumentAsset : Entity
{
    public Guid TenantId { get; set; }
    public Guid? SourceObjectId { get; set; }
    public string StorageUri { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int? PageCount { get; set; }
    public string TextStatus { get; set; } = "pending";
    public string OcrStatus { get; set; } = "pending";
    public string? ExtractionRoute { get; set; }
    public string? ValidityStatus { get; set; }
    public bool? HasTextLayer { get; set; }
    public bool? IsEncrypted { get; set; }

    // Complexity profiling — populated alongside validity inspection.
    // Drives pricing and downstream Doc Intel routing.
    public string? ComplexityTier { get; set; }                  // 'S','M','L','X'
    public string? ComplexityFactorsJson { get; set; }           // jsonb, ["multi_sheet","has_formulas",...]
    public string? ComplexityBlockersJson { get; set; }          // jsonb, ["macros_detected",...]
    public string? MetadataJson { get; set; }                    // jsonb, format-specific details
    public decimal? EstimatedComplexityHours { get; set; }

    // Doc Intelligence layout output — populated by AzureDocumentIntelligenceProvider
    // when extraction routes through cloud OCR. Downstream extractors read
    // LayoutJson when local PdfPig/DocX text extraction was insufficient.
    public string? LayoutJson { get; set; }                      // jsonb
    public string? LayoutProvider { get; set; }                  // 'azure-document-intelligence'
    public string? LayoutModel { get; set; }                     // 'prebuilt-layout' / 'prebuilt-contract'
    public DateTimeOffset? LayoutExtractedAt { get; set; }
    public int? LayoutPageCount { get; set; }

    // Slice 8: regex field extraction onto layout text.
    public string? ExtractedFieldsJson { get; set; }             // jsonb, { fields: [...], reason_codes: [...] }
    public string? ExtractedSubtype { get; set; }                // 'lease_amendment', 'mutual_nda', etc.
    public string? ExtractedSchemaVersion { get; set; }          // 'lease_v1'
    public string? ExtractorName { get; set; }                   // 'lease-extractor-v1'
    public string? ExtractionStatus { get; set; } = "pending";   // 'completed' / 'failed' / 'no_extractor'
    public DateTimeOffset? ExtractionExtractedAt { get; set; }
    public bool? ExtractedIsTemplate { get; set; }
    public bool? ExtractedIsExecuted { get; set; }

    // Full text used by the regex extractor — sourced from layout_json fullText
    // (when Doc Intel ran) or from PdfPig/DocX local extraction otherwise.
    // Stored so the UI's "Original document" pane can show a snippet for
    // formats the browser can't render natively (DOCX, XLSX).
    public string? ExtractedFullText { get; set; }

    // Slice 13: LLM-refined field extraction. Sits alongside the regex output
    // so we can A/B and fall back when the LLM response can't be parsed.
    public string? LlmExtractedFieldsJson { get; set; }
    public string? LlmExtractorModel { get; set; }
    public DateTimeOffset? LlmExtractedAt { get; set; }
    public int? LlmTokensIn { get; set; }
    public int? LlmTokensOut { get; set; }
    public string? LlmExtractionStatus { get; set; }

    // Slice 16: Document Intelligence Brief — stage 1 of the two-pass LLM
    // pipeline. The markdown narrative authored by the model in
    // healthcare-attorney persona; consumed by the UI's "Intelligence Brief"
    // tab and by stage 2 (which extracts structured JSON from this brief
    // into LlmExtractedFieldsJson).
    public string? LlmNarrativeMd { get; set; }
    public string? LlmNarrativeModel { get; set; }
    public int? LlmNarrativeTokensIn { get; set; }
    public int? LlmNarrativeTokensOut { get; set; }
    public DateTimeOffset? LlmNarrativeExtractedAt { get; set; }
    public decimal? LlmNarrativeTemperature { get; set; }
    public string? LlmNarrativeStatus { get; set; }
    public int? LlmNarrativeLatencyMs { get; set; }

    // Slice 20: Counsel's Memo — premium "Legal Advisor Agent" surface.
    // Adversarial corporate-counsel posture: issue spotting, redline
    // suggestions, material-disclosure flags, risk score. Distinct from
    // the Stage-1 narrative brief: the brief describes; the memo recommends.
    public string? LegalMemoMd { get; set; }
    public string? LegalMemoJson { get; set; }                  // jsonb { issues:[], redlines:[], disclosures:[], risk_score, ... }
    public string? LegalMemoModel { get; set; }
    public int? LegalMemoTokensIn { get; set; }
    public int? LegalMemoTokensOut { get; set; }
    public DateTimeOffset? LegalMemoExtractedAt { get; set; }
    public string? LegalMemoStatus { get; set; }
    public int? LegalMemoLatencyMs { get; set; }
    public decimal? LegalMemoRiskScore { get; set; }            // 0-100, sortable for portfolio triage
}

public static class ComplexityTierCodes
{
    public const string Simple   = "S";
    public const string Moderate = "M";
    public const string Large    = "L";
    public const string Extra    = "X";
}

public static class ExtractionRoutes
{
    public const string LocalText = "local_text";
    public const string OcrFirstPages = "ocr_first_pages";
    public const string FullOcr = "full_ocr";
    public const string Skip = "skip";
    public const string ManualReview = "manual_review";
}

public static class ValidityStatuses
{
    public const string Valid = "valid";
    public const string Encrypted = "encrypted";
    public const string Corrupt = "corrupt";
    public const string Unsupported = "unsupported";
    public const string Unknown = "unknown";
}

