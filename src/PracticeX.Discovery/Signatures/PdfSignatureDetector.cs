using System.Text.RegularExpressions;
using PracticeX.Discovery.Contracts;
using UglyToad.PdfPig;

namespace PracticeX.Discovery.Signatures;

/// <summary>
/// Two PDF signature signals stacked:
///  1. Docusign envelope marker — every Docusigned PDF carries a footer like
///     "DocuSign Envelope ID: 7C5E-..." stamped on every page. Producer +
///     Author metadata also frequently include "DocuSign". Strongly indicates
///     a fully-executed agreement.
///  2. AcroForm /Sig fields — native PDF form signature fields. Less common
///     in healthcare contracts but a clean positive signal when present.
///
/// PdfPig version 0.1.14 doesn't expose AcroForm fields cleanly; we scan the
/// raw bytes for the /SigFlags marker as a lightweight fallback. False
/// positives are rare (no innocuous PDF embeds the literal "/Sig" object
/// definition without a signature dictionary).
/// </summary>
public sealed class PdfSignatureDetector : ISignatureDetector
{
    public string Name => "pdf-signature";

    private static readonly Regex DocusignEnvelopeRegex =
        new(@"DocuSign\s+Envelope\s+ID:\s*([A-F0-9-]{8,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AdobeSignRegex =
        new(@"\b(Adobe\s+Sign|EchoSign|Adobe\s+Acrobat\s+Sign)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool CanInspect(string mimeType, string fileName)
    {
        var mime = mimeType?.ToLowerInvariant() ?? string.Empty;
        return mime.Contains("pdf") || (fileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public SignatureReport Inspect(byte[] content, string mimeType, string fileName)
    {
        if (content is null || content.Length == 0)
        {
            return SignatureReport.None;
        }

        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reasons = new List<string>();
        var details = new List<SignatureDetail>();
        var count = 0;

        // 1. Producer / Author metadata + body text — Docusign + Adobe Sign.
        try
        {
            using var doc = PdfDocument.Open(content);
            var producer = doc.Information?.Producer ?? string.Empty;
            var creator = doc.Information?.Creator ?? string.Empty;
            var author = doc.Information?.Author ?? string.Empty;

            var metaBlob = $"{producer} {creator} {author}";
            var docusignInMeta = metaBlob.Contains("docusign", StringComparison.OrdinalIgnoreCase);
            var adobeInMeta = metaBlob.Contains("adobe sign", StringComparison.OrdinalIgnoreCase) ||
                              metaBlob.Contains("acrobat sign", StringComparison.OrdinalIgnoreCase);

            // 2. Body text — Docusign stamps an envelope id footer on every page.
            string? envelopeId = null;
            var pagesToProbe = Math.Min(doc.NumberOfPages, 5);
            for (var i = 1; i <= pagesToProbe; i++)
            {
                try
                {
                    var page = doc.GetPage(i);
                    if (string.IsNullOrWhiteSpace(page.Text)) continue;

                    var match = DocusignEnvelopeRegex.Match(page.Text);
                    if (match.Success)
                    {
                        envelopeId = match.Groups[1].Value;
                        if (providers.Add(SignatureProviderNames.Docusign))
                        {
                            count++;
                            reasons.Add(DiscoveryReasonCodes.DocusignEnvelope);
                            details.Add(new SignatureDetail
                            {
                                Provider = SignatureProviderNames.Docusign,
                                EnvelopeId = envelopeId,
                                PageNumber = i
                            });
                        }
                        break;
                    }
                    if (AdobeSignRegex.IsMatch(page.Text))
                    {
                        if (providers.Add(SignatureProviderNames.Adobe))
                        {
                            count++;
                            reasons.Add(DiscoveryReasonCodes.SignedDocument);
                            details.Add(new SignatureDetail
                            {
                                Provider = SignatureProviderNames.Adobe,
                                PageNumber = i
                            });
                        }
                    }
                }
                catch
                {
                    // Best effort: a page that won't extract isn't fatal.
                }
            }

            // Metadata-only Docusign hit (envelope id may not be in the first 5 pages of text)
            if (docusignInMeta && !providers.Contains(SignatureProviderNames.Docusign))
            {
                providers.Add(SignatureProviderNames.Docusign);
                count++;
                reasons.Add(DiscoveryReasonCodes.DocusignEnvelope);
            }
            if (adobeInMeta && !providers.Contains(SignatureProviderNames.Adobe))
            {
                providers.Add(SignatureProviderNames.Adobe);
                count++;
                reasons.Add(DiscoveryReasonCodes.SignedDocument);
            }
        }
        catch
        {
            // PDF couldn't be opened (corrupt / encrypted) — fall through to byte scan.
        }

        // 3. AcroForm signature fields — byte-level scan as PdfPig 0.1.14
        // doesn't expose AcroForm fields cleanly. We look for the canonical
        // signature-field marker "/FT /Sig" (FieldType Signature) which
        // appears in every AcroForm-signed PDF and nowhere else with that
        // exact combination.
        if (HasAcroFormSignature(content))
        {
            if (providers.Add(SignatureProviderNames.AcroForm))
            {
                count++;
                reasons.Add(DiscoveryReasonCodes.AcroformSignature);
                details.Add(new SignatureDetail { Provider = SignatureProviderNames.AcroForm });
            }
        }

        if (count > 0)
        {
            // Always include the umbrella reason code so UI can chip "Signed" without
            // knowing the provider taxonomy.
            if (!reasons.Contains(DiscoveryReasonCodes.SignedDocument))
            {
                reasons.Insert(0, DiscoveryReasonCodes.SignedDocument);
            }
        }

        return new SignatureReport
        {
            HasSignature = count > 0,
            SignatureCount = count,
            Providers = providers.ToArray(),
            ReasonCodes = reasons,
            Details = details
        };
    }

    private static bool HasAcroFormSignature(byte[] content)
    {
        // Scan the first 1 MB — signature fields live in the catalog dictionary
        // which is near the start of the file in linearised PDFs. 1 MB covers
        // virtually every contract-sized document.
        var max = Math.Min(content.Length, 1024 * 1024);
        var span = content.AsSpan(0, max);
        var marker = "/FT /Sig"u8;
        var altMarker = "/FT/Sig"u8;
        return ContainsAscii(span, marker) || ContainsAscii(span, altMarker);
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0) return true;
        if (haystack.Length < needle.Length) return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
}
