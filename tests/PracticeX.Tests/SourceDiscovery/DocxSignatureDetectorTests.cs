using System.IO.Compression;
using System.Text;
using PracticeX.Discovery.Contracts;
using PracticeX.Discovery.Signatures;

namespace PracticeX.Tests.SourceDiscovery;

public class DocxSignatureDetectorTests
{
    private readonly DocxSignatureDetector _detector = new();

    [Fact]
    public void CanInspect_OfficeContainers()
    {
        Assert.True(_detector.CanInspect("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "x.docx"));
        Assert.True(_detector.CanInspect("application/octet-stream", "letter.docx"));
        Assert.False(_detector.CanInspect("application/pdf", "x.pdf"));
        Assert.False(_detector.CanInspect("text/plain", "x.txt"));
    }

    [Fact]
    public void Inspect_PlainDocxNoSignatureLine_ReturnsNone()
    {
        var docx = BuildDocx(documentXml: "<w:document><w:body><w:p><w:r><w:t>Hello</w:t></w:r></w:p></w:body></w:document>");

        var report = _detector.Inspect(docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "letter.docx");

        Assert.False(report.HasSignature);
    }

    [Fact]
    public void Inspect_DocxWithSignatureLine_FlagsDocx()
    {
        // Word's "Signature Line" inserts a w:object with a signatureLine element.
        var docXml = "<w:document><w:body><w:p><w:r><w:object><w:signatureLine/></w:object></w:r></w:p></w:body></w:document>";
        var docx = BuildDocx(documentXml: docXml);

        var report = _detector.Inspect(docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "letter.docx");

        Assert.True(report.HasSignature);
        Assert.Contains(SignatureProviderNames.Docx, report.Providers);
        Assert.Contains(DiscoveryReasonCodes.DocxSignatureLine, report.ReasonCodes);
        Assert.Contains(DiscoveryReasonCodes.SignedDocument, report.ReasonCodes);
    }

    [Fact]
    public void Inspect_DocxWithDocusignCustomXml_FlagsDocusign()
    {
        // Docusign embeds an envelope record under customXml/.
        var docx = BuildDocx(
            documentXml: "<w:document><w:body><w:p><w:r><w:t>Hello</w:t></w:r></w:p></w:body></w:document>",
            extraEntries: new Dictionary<string, string>
            {
                ["customXml/item1.xml"] = "<ds:envelope xmlns:ds=\"docusign\"><EnvelopeId>ABCD-1234</EnvelopeId></ds:envelope>"
            });

        var report = _detector.Inspect(docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "letter.docx");

        Assert.True(report.HasSignature);
        Assert.Contains(SignatureProviderNames.Docusign, report.Providers);
        Assert.Contains(DiscoveryReasonCodes.DocusignEnvelope, report.ReasonCodes);
        var envelopeDetail = report.Details.FirstOrDefault(d => d.Provider == SignatureProviderNames.Docusign);
        Assert.NotNull(envelopeDetail);
        Assert.Equal("ABCD-1234", envelopeDetail!.EnvelopeId);
    }

    [Fact]
    public void Inspect_DocxWithNativeXmlSignature_FlagsNative()
    {
        var docx = BuildDocx(
            documentXml: "<w:document><w:body><w:p><w:r><w:t>Hi</w:t></w:r></w:p></w:body></w:document>",
            extraEntries: new Dictionary<string, string>
            {
                ["_xmlsignatures/sig1.xml"] = "<Signature/>"
            });

        var report = _detector.Inspect(docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "letter.docx");

        Assert.True(report.HasSignature);
        Assert.Contains(SignatureProviderNames.Native, report.Providers);
    }

    [Fact]
    public void Inspect_NotZip_ReturnsNone()
    {
        var report = _detector.Inspect(Encoding.UTF8.GetBytes("not a zip"), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "x.docx");
        Assert.False(report.HasSignature);
    }

    private static byte[] BuildDocx(string documentXml, IReadOnlyDictionary<string, string>? extraEntries = null)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<?xml version=\"1.0\"?><Types/>");
            WriteEntry(archive, "word/document.xml", documentXml);
            if (extraEntries is not null)
            {
                foreach (var (path, content) in extraEntries)
                {
                    WriteEntry(archive, path, content);
                }
            }
        }
        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(content);
    }
}
