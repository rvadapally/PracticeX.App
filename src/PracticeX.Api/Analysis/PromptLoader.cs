using System.Collections.Concurrent;
using System.Reflection;

namespace PracticeX.Api.Analysis;

/// <summary>
/// Loads stage-1 (narrative) and stage-2 (extraction) prompt templates from
/// embedded resources at <c>PracticeX.Api.Analysis.Prompts.Stage*_*.md</c>.
/// Templates are cached after first read; substitution is positional via
/// <c>{KEY}</c> tokens. Family selection comes from the document's
/// candidate type — see <see cref="ResolveFamily"/>.
/// </summary>
public static class PromptLoader
{
    private static readonly ConcurrentDictionary<string, string> _cache = new();
    private static readonly Assembly _asm = typeof(PromptLoader).Assembly;

    public static string ResolveFamily(string candidateType) => candidateType switch
    {
        "lease" or "lease_amendment" or "lease_loi" or "sublease" => "Lease",
        "nda" => "Nda",
        "employee_agreement" or "amendment" => "Employment",
        "call_coverage_agreement" => "CallCoverage",
        _ => "Generic"
    };

    public static string LoadStage1(string candidateType) =>
        Load($"Stage1_{ResolveFamily(candidateType)}");

    public static string LoadStage2(string candidateType) =>
        Load($"Stage2_{ResolveFamily(candidateType)}");

    public static string LoadStage3() => Load("Stage3_Portfolio");

    /// <summary>
    /// Slice 20: Counsel's Memo prompts. Master prompt is the same across
    /// families; the family-specific overlay is injected as a section into
    /// the master template.
    /// </summary>
    public static string LoadLegalMemoMaster() => Load("LegalMemo_Master");
    public static string LoadLegalMemoFamilyOverlay(string candidateType) =>
        Load($"LegalMemo_{ResolveFamily(candidateType)}");
    public static string LoadLegalMemoJson() => Load("LegalMemo_Json");
    public static string LoadCounselBrief() => Load("CounselBrief");

    private static string Load(string baseName)
    {
        return _cache.GetOrAdd(baseName, name =>
        {
            var resourceName = $"PracticeX.Api.Analysis.Prompts.{name}.md";
            using var stream = _asm.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded prompt resource not found: {resourceName}. " +
                    $"Check Analysis/Prompts/{name}.md exists and the .csproj has " +
                    $"<EmbeddedResource Include=\"Analysis\\Prompts\\*.md\" />.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
    }

    /// <summary>
    /// Substitutes <c>{KEY}</c> tokens in <paramref name="template"/> using
    /// the provided dictionary. Unknown tokens are left in place — they
    /// surface during prompt review and never silently disappear.
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        var result = template;
        foreach (var (key, value) in values)
        {
            result = result.Replace("{" + key + "}", value ?? string.Empty, StringComparison.Ordinal);
        }
        return result;
    }
}
