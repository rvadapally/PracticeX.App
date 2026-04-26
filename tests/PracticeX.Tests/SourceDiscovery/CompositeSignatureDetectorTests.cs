using PracticeX.Discovery.Contracts;
using PracticeX.Discovery.Signatures;

namespace PracticeX.Tests.SourceDiscovery;

public class CompositeSignatureDetectorTests
{
    [Fact]
    public void EmptyChain_ReturnsNone()
    {
        var composite = new CompositeSignatureDetector(Array.Empty<ISignatureDetector>());
        var report = composite.Inspect([1, 2, 3], "application/pdf", "x.pdf");
        Assert.False(report.HasSignature);
    }

    [Fact]
    public void NoBytes_ReturnsNone()
    {
        var composite = new CompositeSignatureDetector(new ISignatureDetector[] { new StubAlwaysHits() });
        var report = composite.Inspect([], "application/pdf", "x.pdf");
        Assert.False(report.HasSignature);
    }

    [Fact]
    public void MergesFromMultipleDetectors_DedupingProviders()
    {
        var composite = new CompositeSignatureDetector(new ISignatureDetector[]
        {
            new StubAlwaysHits(SignatureProviderNames.Docusign),
            new StubAlwaysHits(SignatureProviderNames.AcroForm),
            new StubAlwaysHits(SignatureProviderNames.Docusign)   // duplicate provider — should not double-count provider list
        });

        var report = composite.Inspect([1, 2, 3], "application/pdf", "x.pdf");

        Assert.True(report.HasSignature);
        Assert.Equal(3, report.SignatureCount); // counts sum across detectors
        Assert.Equal(2, report.Providers.Count); // de-duped
        Assert.Contains(SignatureProviderNames.Docusign, report.Providers);
        Assert.Contains(SignatureProviderNames.AcroForm, report.Providers);
        Assert.Contains(DiscoveryReasonCodes.SignedDocument, report.ReasonCodes);
        // Umbrella code first.
        Assert.Equal(DiscoveryReasonCodes.SignedDocument, report.ReasonCodes[0]);
    }

    [Fact]
    public void IgnoresDetectorsThatCannotInspect()
    {
        var composite = new CompositeSignatureDetector(new ISignatureDetector[]
        {
            new StubAlwaysHits(SignatureProviderNames.Docusign, canInspect: false),
            new StubAlwaysHits(SignatureProviderNames.AcroForm)
        });
        var report = composite.Inspect([1, 2, 3], "application/pdf", "x.pdf");

        Assert.True(report.HasSignature);
        Assert.Single(report.Providers);
        Assert.Contains(SignatureProviderNames.AcroForm, report.Providers);
    }

    private sealed class StubAlwaysHits : ISignatureDetector
    {
        private readonly string _provider;
        private readonly bool _canInspect;

        public StubAlwaysHits(string provider = SignatureProviderNames.Docusign, bool canInspect = true)
        {
            _provider = provider;
            _canInspect = canInspect;
        }

        public string Name => $"stub-{_provider}";
        public bool CanInspect(string mimeType, string fileName) => _canInspect;

        public SignatureReport Inspect(byte[] content, string mimeType, string fileName) => new()
        {
            HasSignature = true,
            SignatureCount = 1,
            Providers = [_provider],
            ReasonCodes = [DiscoveryReasonCodes.SignedDocument]
        };
    }
}
