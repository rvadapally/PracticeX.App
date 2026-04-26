namespace PracticeX.Discovery.Signatures;

/// <summary>
/// Inspects bytes for signs that the document is signed — Docusign envelope
/// IDs, PDF AcroForm /Sig fields, or DOCX signature-line content controls.
/// Each detector handles one or two formats; the composite walks the chain
/// and merges results.
///
/// Pure logic, no DI, no DB. Same impl runs in the cloud orchestrator and
/// (later) the desktop agent's local-prefilter mode.
/// </summary>
public interface ISignatureDetector
{
    /// <summary>Stable name used in logs and audit trails.</summary>
    string Name { get; }

    /// <summary>Returns false when this detector doesn't handle this mime/extension.</summary>
    bool CanInspect(string mimeType, string fileName);

    SignatureReport Inspect(byte[] content, string mimeType, string fileName);
}

public sealed record SignatureReport
{
    /// <summary>True if any signature, signature line, or e-sign envelope was found.</summary>
    public bool HasSignature { get; init; }

    /// <summary>Number of distinct signature artefacts (fields, lines, certs) detected.</summary>
    public int SignatureCount { get; init; }

    /// <summary>
    /// Provider tags from <see cref="Contracts.SignatureProviderNames"/> —
    /// e.g. ["docusign", "acroform"].
    /// </summary>
    public IReadOnlyList<string> Providers { get; init; } = Array.Empty<string>();

    /// <summary>Reason-code strings to surface alongside the classifier output.</summary>
    public IReadOnlyList<string> ReasonCodes { get; init; } = Array.Empty<string>();

    /// <summary>Optional details — signer names extracted from cert dictionaries, page numbers, etc.</summary>
    public IReadOnlyList<SignatureDetail> Details { get; init; } = Array.Empty<SignatureDetail>();

    public static readonly SignatureReport None = new();
}

public sealed record SignatureDetail
{
    public required string Provider { get; init; }
    public string? SignerName { get; init; }
    public int? PageNumber { get; init; }
    public string? EnvelopeId { get; init; }
    public DateTimeOffset? SignedAtUtc { get; init; }
}
