using PracticeX.Discovery.Contracts;

namespace PracticeX.Discovery.Signatures;

/// <summary>
/// Default ISignatureDetector — runs every registered detector that can
/// inspect the file and merges their reports. Used by the orchestrator
/// (cloud) and the agent's local-prefilter mode.
/// </summary>
public sealed class CompositeSignatureDetector(IEnumerable<ISignatureDetector> detectors) : ISignatureDetector
{
    private readonly IReadOnlyList<ISignatureDetector> _detectors =
        detectors.Where(d => d is not CompositeSignatureDetector).ToList();

    public string Name => "composite";

    public bool CanInspect(string mimeType, string fileName) =>
        _detectors.Any(d => d.CanInspect(mimeType, fileName));

    public SignatureReport Inspect(byte[] content, string mimeType, string fileName)
    {
        if (_detectors.Count == 0 || content is null || content.Length == 0)
        {
            return SignatureReport.None;
        }

        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var details = new List<SignatureDetail>();
        var count = 0;

        foreach (var detector in _detectors)
        {
            if (!detector.CanInspect(mimeType, fileName)) continue;
            var report = detector.Inspect(content, mimeType, fileName);
            if (!report.HasSignature) continue;

            foreach (var p in report.Providers) providers.Add(p);
            foreach (var r in report.ReasonCodes) reasons.Add(r);
            foreach (var d in report.Details) details.Add(d);
            count += report.SignatureCount;
        }

        if (count > 0)
        {
            // Make sure the umbrella code shows up first, before per-provider codes.
            var ordered = new List<string> { DiscoveryReasonCodes.SignedDocument };
            ordered.AddRange(reasons.Where(r => r != DiscoveryReasonCodes.SignedDocument));
            return new SignatureReport
            {
                HasSignature = true,
                SignatureCount = count,
                Providers = providers.ToArray(),
                ReasonCodes = ordered,
                Details = details
            };
        }

        return SignatureReport.None;
    }
}
