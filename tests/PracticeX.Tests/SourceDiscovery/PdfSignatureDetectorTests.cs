using System.Text;
using PracticeX.Discovery.Contracts;
using PracticeX.Discovery.Signatures;

namespace PracticeX.Tests.SourceDiscovery;

public class PdfSignatureDetectorTests
{
    private readonly PdfSignatureDetector _detector = new();

    [Fact]
    public void Inspect_NotPdf_ReturnsNone()
    {
        var report = _detector.Inspect(Encoding.UTF8.GetBytes("hello"), "text/plain", "memo.txt");

        Assert.False(report.HasSignature);
        Assert.Empty(report.Providers);
    }

    [Fact]
    public void CanInspect_OnlyPdf()
    {
        Assert.True(_detector.CanInspect("application/pdf", "x.pdf"));
        Assert.True(_detector.CanInspect("application/octet-stream", "x.pdf"));
        Assert.False(_detector.CanInspect("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "x.docx"));
        Assert.False(_detector.CanInspect("text/plain", "x.txt"));
    }

    [Fact]
    public void Inspect_AcroFormSigByteMarker_FlagsAcroFormSignature()
    {
        // Synthetic byte stream containing only the AcroForm signature-field marker.
        // Real PDFs always include the "/FT /Sig" entry inside the AcroForm field
        // dictionary; we do a substring scan, so a synthetic byte stream that
        // contains the marker (and nothing else) should still trip the detector.
        var fakePdf = Encoding.ASCII.GetBytes("%PDF-1.7\n/FT /Sig\nstuff\n%%EOF");

        var report = _detector.Inspect(fakePdf, "application/pdf", "synthetic.pdf");

        Assert.True(report.HasSignature);
        Assert.Equal(1, report.SignatureCount);
        Assert.Contains(SignatureProviderNames.AcroForm, report.Providers);
        Assert.Contains(DiscoveryReasonCodes.AcroformSignature, report.ReasonCodes);
        Assert.Contains(DiscoveryReasonCodes.SignedDocument, report.ReasonCodes);
    }

    [Fact]
    public void Inspect_BogusPdfBytes_ReturnsNoneNotCrash()
    {
        // Bytes that don't even start with %PDF — detector must not throw.
        var report = _detector.Inspect(Encoding.UTF8.GetBytes("not a pdf"), "application/pdf", "fake.pdf");

        Assert.False(report.HasSignature);
        Assert.Empty(report.Providers);
    }

    [Fact]
    public void Inspect_EmptyContent_ReturnsNone()
    {
        var report = _detector.Inspect([], "application/pdf", "empty.pdf");

        Assert.False(report.HasSignature);
        Assert.Empty(report.ReasonCodes);
    }
}
