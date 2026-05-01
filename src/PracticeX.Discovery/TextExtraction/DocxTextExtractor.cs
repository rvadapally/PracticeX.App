using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace PracticeX.Discovery.TextExtraction;

/// <summary>
/// DOCX text extractor backed by DocumentFormat.OpenXml 3.2.0. DOCX has no
/// native page concept (pagination happens at render time), so we emit a
/// single <see cref="ExtractedPage"/> with <c>PageNumber = 1</c> and ignore
/// <c>maxPages</c>.
///
/// Heading detection uses paragraph style ids — Word's built-in styles
/// "Heading1", "Heading2", … carry the level in the trailing digit. Custom
/// styles named differently won't be picked up; that's fine for v1.
///
/// Returns <see cref="TextExtractionResult.Empty"/> with <c>Notes</c> on any
/// IO/parse failure. Never throws.
/// </summary>
public sealed class DocxTextExtractor : IDocumentTextExtractor
{
    public string Name => "docx-text";

    public bool CanExtract(string mimeType, string fileName)
    {
        var mime = mimeType?.ToLowerInvariant() ?? string.Empty;
        if (mime.Contains("vnd.openxmlformats-officedocument.wordprocessingml.document"))
        {
            return true;
        }
        var name = fileName?.ToLowerInvariant() ?? string.Empty;
        return name.EndsWith(".docx") || name.EndsWith(".dotx") || name.EndsWith(".docm");
    }

    public TextExtractionResult Extract(byte[] content, string mimeType, string fileName, int? maxPages = null)
    {
        if (content is null || content.Length == 0)
        {
            return TextExtractionResult.Empty with { ExtractorName = Name, Notes = "empty" };
        }

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var doc = WordprocessingDocument.Open(stream, isEditable: false);

            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null)
            {
                return TextExtractionResult.Empty with { ExtractorName = Name, Notes = "no-body" };
            }

            var paragraphs = body.Descendants<Paragraph>().ToList();
            var paragraphTexts = new List<string>(paragraphs.Count);
            var headings = new List<ExtractedHeading>();

            foreach (var paragraph in paragraphs)
            {
                // ExtractParagraphText walks runs explicitly and (a) skips
                // text inside <w:del> tracked-change deletions, (b) skips
                // runs with <w:strike/> or <w:dstrike/> direct formatting.
                // paragraph.InnerText concatenates everything indiscriminately
                // and produces garbage like "April 1420, 2026" when "14" is
                // struck through and "20" is the live replacement.
                var text = ExtractParagraphText(paragraph);
                paragraphTexts.Add(text);

                var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                if (!string.IsNullOrEmpty(styleId) &&
                    styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
                {
                    var level = ParseHeadingLevel(styleId);
                    headings.Add(new ExtractedHeading(text, PageNumber: 1, level));
                }
            }

            var fullText = string.Join("\n", paragraphTexts);

            return new TextExtractionResult
            {
                FullText = fullText,
                Pages = [new ExtractedPage(1, fullText)],
                Headings = headings,
                ExtractorName = Name,
                Truncated = false
            };
        }
        catch (Exception ex)
        {
            return TextExtractionResult.Empty with { ExtractorName = Name, Notes = ex.Message };
        }
    }

    /// <summary>
    /// Extracts the live text from a paragraph, filtering out:
    ///   - Tracked-change deletions: any run inside a &lt;w:del&gt; element
    ///     (its text lives in &lt;w:delText&gt;, the OpenXml-typed class
    ///     <see cref="DeletedText"/>, which we never read).
    ///   - Direct strikethrough formatting: runs whose RunProperties carry
    ///     &lt;w:strike/&gt; or &lt;w:dstrike/&gt; without an explicit
    ///     <c>val="false"</c>.
    /// Inserted runs (&lt;w:ins&gt;) and tab/break elements are kept.
    /// </summary>
    private static string ExtractParagraphText(Paragraph paragraph)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var run in paragraph.Descendants<Run>())
        {
            // Skip if this run lives inside a tracked-change deletion.
            if (run.Ancestors<DeletedRun>().Any())
            {
                continue;
            }

            // Skip runs with strikethrough direct formatting. OOXML toggle
            // semantics: element absent = off; element present with no Val
            // attribute = on; Val explicitly false = off; anything else = on.
            var rp = run.RunProperties;
            if (rp?.Strike != null && (rp.Strike.Val?.Value ?? true)) continue;
            if (rp?.DoubleStrike != null && (rp.DoubleStrike.Val?.Value ?? true)) continue;

            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    case Text t:
                        sb.Append(t.Text);
                        break;
                    case TabChar:
                        sb.Append('\t');
                        break;
                    case Break br when br.Type?.Value == BreakValues.TextWrapping || br.Type is null:
                        sb.Append('\n');
                        break;
                    // DeletedText (<w:delText>) is intentionally skipped —
                    // it's the typed class for content inside <w:del>.
                }
            }
        }
        return sb.ToString();
    }

    private static int ParseHeadingLevel(string styleId)
    {
        // "Heading1" → 1, "Heading2" → 2, …, "Heading" alone → 1.
        for (var i = styleId.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(styleId[i]))
            {
                if (i == styleId.Length - 1) return 1;
                return int.TryParse(styleId.AsSpan(i + 1), out var level) ? level : 1;
            }
        }
        return 1;
    }
}
