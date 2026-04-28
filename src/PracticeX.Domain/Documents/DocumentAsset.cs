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

