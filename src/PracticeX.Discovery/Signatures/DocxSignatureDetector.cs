using System.IO.Compression;
using System.Text;
using System.Xml;
using PracticeX.Discovery.Contracts;

namespace PracticeX.Discovery.Signatures;

/// <summary>
/// Detects signature signals in DOCX containers:
///  1. <c>w:object</c> elements with the SignatureLine OLE class — Word's
///     "Signature Line" feature. Strong positive signal that someone added
///     an explicit "sign here" placeholder.
///  2. Docusign customXml parts under <c>customXml/</c> — Docusign embeds
///     <c>&lt;ds:envelope&gt;</c> when the contract has been processed
///     through their workflow.
///  3. <c>_xmlsignatures/</c> parts — Word's native digital signatures.
///
/// Operates directly on the zip archive; no OpenXml dependency, so this lib
/// stays small.
/// </summary>
public sealed class DocxSignatureDetector : ISignatureDetector
{
    public string Name => "docx-signature";

    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04]; // PK\x03\x04

    public bool CanInspect(string mimeType, string fileName)
    {
        var mime = mimeType?.ToLowerInvariant() ?? string.Empty;
        if (mime.Contains("officedocument") || mime.Contains("msword") || mime.Contains("ms-excel"))
        {
            return true;
        }
        var name = fileName?.ToLowerInvariant() ?? string.Empty;
        return name.EndsWith(".docx") || name.EndsWith(".dotx") || name.EndsWith(".docm");
    }

    public SignatureReport Inspect(byte[] content, string mimeType, string fileName)
    {
        if (content is null || content.Length < ZipSignature.Length || !StartsWith(content, ZipSignature))
        {
            return SignatureReport.None;
        }

        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reasons = new List<string>();
        var details = new List<SignatureDetail>();
        var count = 0;

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;

                // 1. Native digital signature parts — exists when the document
                // was signed via "File → Info → Protect Document → Add Digital Signature".
                if (name.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    if (providers.Add(SignatureProviderNames.Native))
                    {
                        count++;
                        reasons.Add(DiscoveryReasonCodes.DocxSignatureLine);
                        details.Add(new SignatureDetail { Provider = SignatureProviderNames.Native });
                    }
                }

                // 2. Docusign customXml — envelope wrapper.
                if (name.StartsWith("customXml/", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    var xml = ReadEntryText(entry);
                    if (xml.Contains("docusign", StringComparison.OrdinalIgnoreCase) ||
                        xml.Contains("envelope", StringComparison.OrdinalIgnoreCase))
                    {
                        if (providers.Add(SignatureProviderNames.Docusign))
                        {
                            count++;
                            reasons.Add(DiscoveryReasonCodes.DocusignEnvelope);
                            details.Add(new SignatureDetail
                            {
                                Provider = SignatureProviderNames.Docusign,
                                EnvelopeId = TryExtractEnvelopeId(xml)
                            });
                        }
                    }
                }
            }

            // 3. word/document.xml — scan for w:object with SignatureLine class.
            var docEntry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase));
            if (docEntry is not null)
            {
                var docXml = ReadEntryText(docEntry);
                if (docXml.Contains("signatureLine", StringComparison.OrdinalIgnoreCase) ||
                    docXml.Contains("AddInData", StringComparison.OrdinalIgnoreCase) &&
                    docXml.Contains("CLSID:00020D03-0000-0000-C000-000000000046", StringComparison.OrdinalIgnoreCase))
                {
                    if (providers.Add(SignatureProviderNames.Docx))
                    {
                        count++;
                        reasons.Add(DiscoveryReasonCodes.DocxSignatureLine);
                        details.Add(new SignatureDetail { Provider = SignatureProviderNames.Docx });
                    }
                }
            }
        }
        catch
        {
            return SignatureReport.None;
        }

        if (count > 0 && !reasons.Contains(DiscoveryReasonCodes.SignedDocument))
        {
            reasons.Insert(0, DiscoveryReasonCodes.SignedDocument);
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

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        try
        {
            using var s = entry.Open();
            using var reader = new StreamReader(s, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? TryExtractEnvelopeId(string xml)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            // Best effort: check common element names.
            foreach (var tag in new[] { "EnvelopeId", "envelopeId", "envelope_id" })
            {
                var node = doc.GetElementsByTagName(tag).Cast<XmlNode>().FirstOrDefault();
                if (node is not null && !string.IsNullOrWhiteSpace(node.InnerText))
                {
                    return node.InnerText.Trim();
                }
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private static bool StartsWith(byte[] content, byte[] signature)
    {
        if (content.Length < signature.Length) return false;
        for (var i = 0; i < signature.Length; i++)
        {
            if (content[i] != signature[i]) return false;
        }
        return true;
    }
}
