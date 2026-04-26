using System.Text.RegularExpressions;
using PracticeX.Discovery.FieldExtraction.Helpers;
using PracticeX.Discovery.Schemas;

namespace PracticeX.Discovery.FieldExtraction;

/// <summary>
/// v1 NDA-family field extractor — regex-only scaffold. Detects the subtype
/// (bilateral individual / mutual org / investor / advisor / demo participant
/// templates), pulls common fields (effective date, governing law, parties,
/// permitted purpose, confidential information definition, exclusions,
/// term, signature block) plus a few NDA-specific fields (non-solicit
/// months, non-disparagement). LLM-quality extraction is v2.
/// </summary>
public sealed class NdaExtractor : IContractFieldExtractor
{
    public string Name => "nda-extractor-v1";
    public string SchemaVersion => NdaSchemaV1Constants.Version;

    public bool CanExtract(string subtypeOrCandidateType)
    {
        if (string.IsNullOrWhiteSpace(subtypeOrCandidateType)) return false;
        var key = subtypeOrCandidateType.Trim();
        if (string.Equals(key, NdaSchemaV1Constants.CandidateType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return NdaSchemaV1Constants.Subtypes.All.Any(
            s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase));
    }

    public FieldExtractionResult Extract(FieldExtractionInput input)
    {
        var fileName = input.FileName ?? string.Empty;
        var body = input.FullText ?? string.Empty;
        var subtype = DetectSubtype(fileName, body);

        var fields = new Dictionary<string, ExtractedField>(StringComparer.OrdinalIgnoreCase);
        var reasons = new List<string> { "nda_extractor_v1", $"subtype_detected:{subtype}" };
        var isTemplate = NdaSchemaV1Constants.Subtypes.Templates.Contains(subtype);
        var isExecuted = false;

        // ---- Effective date ----
        var (effectiveDate, effectiveCitation, effectivePlaceholder) = ExtractEffectiveDate(body);
        fields["effective_date"] = new ExtractedField
        {
            Name = "effective_date",
            Value = effectiveDate,
            Confidence = effectiveDate is not null ? 0.85m : (effectivePlaceholder ? 0.3m : 0.0m),
            SourceCitation = effectiveCitation
        };
        if (effectivePlaceholder) isTemplate = true;

        // ---- Governing law ----
        var (governingLaw, lawCitation, lawPlaceholder) = ExtractGoverningLaw(body);
        fields["governing_law"] = new ExtractedField
        {
            Name = "governing_law",
            Value = governingLaw,
            Confidence = governingLaw is not null ? 0.85m : (lawPlaceholder ? 0.3m : 0.0m),
            SourceCitation = lawCitation
        };
        if (lawPlaceholder) isTemplate = true;

        // ---- Parties ----
        var parties = ExtractParties(body, subtype);
        fields["parties"] = new ExtractedField
        {
            Name = "parties",
            Value = parties,
            Confidence = parties.Count > 0 ? 0.5m : 0.0m
        };

        // ---- Permitted purpose ----
        var permittedPurpose = ExtractPermittedPurpose(body, subtype);
        fields["permitted_purpose"] = new ExtractedField
        {
            Name = "permitted_purpose",
            Value = permittedPurpose,
            Confidence = permittedPurpose is not null ? 0.5m : 0.0m
        };

        // ---- Confidential information definition ----
        var ciDefinition = ExtractConfidentialInformationDefinition(body);
        fields["confidential_information_definition"] = new ExtractedField
        {
            Name = "confidential_information_definition",
            Value = ciDefinition,
            Confidence = ciDefinition is not null ? 0.4m : 0.0m
        };

        // ---- Exclusions (best-effort) ----
        var exclusions = ExtractExclusions(body);
        fields["exclusions"] = new ExtractedField
        {
            Name = "exclusions",
            Value = exclusions,
            Confidence = exclusions.Count > 0 ? 0.4m : 0.0m
        };

        // ---- is_mutual ----
        var isMutual = subtype == NdaSchemaV1Constants.Subtypes.MutualOrg
            || (body.Contains("each Party", StringComparison.OrdinalIgnoreCase)
                && body.Contains("discloses", StringComparison.OrdinalIgnoreCase));
        fields["is_mutual"] = new ExtractedField
        {
            Name = "is_mutual",
            Value = isMutual,
            Confidence = 0.7m
        };

        // ---- Signature block ----
        var signatures = new List<SignatureRecord>();
        if (!string.IsNullOrWhiteSpace(input.SignatureProvider))
        {
            signatures.Add(new SignatureRecord(
                SignerName: "",
                SignerTitle: null,
                SignerRole: null,
                SignedAtUtc: null,
                SignatureProvider: input.SignatureProvider!,
                EnvelopeId: input.DocusignEnvelopeId,
                PageNumber: null));
            isExecuted = true;
        }
        fields["signature_block"] = new ExtractedField
        {
            Name = "signature_block",
            Value = signatures,
            Confidence = isExecuted ? 0.85m : 0.0m
        };

        // ---- Term ----
        var term = ExtractTerm(body, isTemplate, isExecuted);
        fields["term"] = new ExtractedField
        {
            Name = "term",
            Value = term,
            Confidence = term is not null ? (isExecuted ? 0.6m : 0.5m) : 0.0m
        };

        // ---- Non-solicit term months ----
        var nonSolicitMonths = ExtractNonSolicitMonths(body);
        fields["non_solicit_term_months"] = new ExtractedField
        {
            Name = "non_solicit_term_months",
            Value = nonSolicitMonths,
            Confidence = nonSolicitMonths is not null ? 0.85m : 0.0m
        };

        // ---- Non-disparagement ----
        var nonDisparagement = body.Contains("non-disparagement", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(body, @"shall\s+not\s+disparage", RegexOptions.IgnoreCase);
        fields["non_disparagement"] = new ExtractedField
        {
            Name = "non_disparagement",
            Value = nonDisparagement,
            Confidence = 0.7m
        };

        // is_template fallback: placeholder text in critical fields.
        if (!isTemplate && (RegexHelpers.LooksLikePlaceholder(effectiveCitation)
                             || RegexHelpers.LooksLikePlaceholder(lawCitation)))
        {
            isTemplate = true;
        }

        fields["is_template"] = new ExtractedField
        {
            Name = "is_template",
            Value = isTemplate,
            Confidence = 0.9m
        };
        fields["is_executed"] = new ExtractedField
        {
            Name = "is_executed",
            Value = isExecuted,
            Confidence = 0.9m
        };

        if (isTemplate) reasons.Add("nda_template");
        if (isExecuted) reasons.Add("signature_attached");

        if (!isTemplate)
        {
            var missing = CountMissingKeyFields(fields);
            if (missing >= 2) reasons.Add("extraction_partial");
        }

        return new FieldExtractionResult
        {
            SchemaVersion = SchemaVersion,
            Subtype = subtype,
            Fields = fields,
            IsTemplate = isTemplate,
            IsExecuted = isExecuted,
            ReasonCodes = reasons
        };
    }

    private static string DetectSubtype(string fileName, string body)
    {
        var fn = fileName.ToLowerInvariant();
        var bodyLower = body.ToLowerInvariant();

        var hasInvestor = fn.Contains("investor") || bodyLower.Contains("investor");
        var hasAdvisor = fn.Contains("advisor") || bodyLower.Contains("advisor");
        var hasDemoParticipant = fn.Contains("demo participant") || fn.Contains("demo_participant")
            || bodyLower.Contains("demo participant");
        var hasNda = fn.Contains("nda") || bodyLower.Contains("nda")
            || bodyLower.Contains("non-disclosure") || bodyLower.Contains("non disclosure")
            || bodyLower.Contains("nondisclosure");

        // Template signals: only treat a doc as a *_template if it actually
        // looks like a template (placeholders, template filename, etc.).
        // Otherwise the word "advisor"/"investor" appearing in an executed
        // bilateral NDA would mis-route it to the template subtype.
        var looksLikeTemplate = fn.Contains("templates/") || fn.Contains("template")
            || body.Contains("[Counterparty Name]", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(body, @"_{3,}\s*Date:", RegexOptions.IgnoreCase);

        if (hasDemoParticipant && (looksLikeTemplate || !HasExecutedSignals(body)))
        {
            return NdaSchemaV1Constants.Subtypes.DemoParticipantTemplate;
        }

        if (looksLikeTemplate)
        {
            if (hasInvestor && hasNda) return NdaSchemaV1Constants.Subtypes.InvestorTemplate;
            if (hasAdvisor && hasNda) return NdaSchemaV1Constants.Subtypes.AdvisorTemplate;
            if (hasDemoParticipant) return NdaSchemaV1Constants.Subtypes.DemoParticipantTemplate;
            // Ambiguous template — investor is the most common.
            return NdaSchemaV1Constants.Subtypes.InvestorTemplate;
        }

        if (bodyLower.Contains("mutual confidential") || bodyLower.Contains("mutual non-disclosure")
            || bodyLower.Contains("mutual nondisclosure") || fn.Contains("mutual"))
        {
            return NdaSchemaV1Constants.Subtypes.MutualOrg;
        }

        return NdaSchemaV1Constants.Subtypes.BilateralIndividual;
    }

    private static bool HasExecutedSignals(string body)
    {
        // Concrete date or "between X and Y" → looks executed.
        if (RegexHelpers.DateLong.IsMatch(body) || RegexHelpers.DateSlash.IsMatch(body)
            || RegexHelpers.DateIso.IsMatch(body))
        {
            return true;
        }
        if (BetweenPartiesRegex.IsMatch(body)) return true;
        return false;
    }

    private static readonly Regex EffectiveDateRegex = new(
        @"(?<lead>Effective Date:?\s+|effective as of\s+|dated\s+)(?<value>[A-Z][a-z]+\s+\d{1,2},?\s+\d{4}|\d{1,2}/\d{1,2}/\d{4}|\d{4}-\d{2}-\d{2}|_{3,}|\[[^\]]+\])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static (DateTimeOffset? value, string? citation, bool placeholder) ExtractEffectiveDate(string body)
    {
        var match = EffectiveDateRegex.Match(body);
        if (!match.Success) return (null, null, false);
        var raw = match.Groups["value"].Value;
        if (RegexHelpers.LooksLikePlaceholder(raw))
        {
            return (null, "effective_date placeholder", true);
        }
        var parsed = RegexHelpers.ParseDate(raw);
        return (parsed, parsed is null ? null : $"effective date: {raw}", false);
    }

    private static readonly Regex GoverningLawRegex = new(
        @"governed by the laws of (the )?(State of )?(?<value>\[[^\]]+\]|_{3,}|[A-Z][A-Za-z]+)",
        RegexOptions.Compiled);

    private static (string? value, string? citation, bool placeholder) ExtractGoverningLaw(string body)
    {
        var match = GoverningLawRegex.Match(body);
        if (!match.Success) return (null, null, false);
        var raw = match.Groups["value"].Value.Trim();
        if (RegexHelpers.LooksLikePlaceholder(raw))
        {
            return (null, "governing_law placeholder", true);
        }
        return (raw, $"governing law: {raw}", false);
    }

    private static readonly Regex BetweenPartiesRegex = new(
        @"between\s+(?<a>[A-Z][\w&\.\s,]+?)(\s*\(""(?<aRole>[^""]+)""\))?\s+and\s+(?<b>[A-Z][\w&\.\s,]+?)(\s*\(""(?<bRole>[^""]+)""\))?[\.\,]",
        RegexOptions.Compiled);

    private static List<PartyRecord> ExtractParties(string body, string subtype)
    {
        // Templates: parties[0] = "Synexar, Inc." (templates are ours), parties[1] = null.
        if (NdaSchemaV1Constants.Subtypes.Templates.Contains(subtype))
        {
            return new List<PartyRecord>
            {
                new("organization", "Synexar, Inc.", null, null, null, null)
            };
        }

        var list = new List<PartyRecord>();
        var match = BetweenPartiesRegex.Match(body);
        if (!match.Success) return list;
        list.Add(new PartyRecord("organization", match.Groups["a"].Value.Trim(),
            NullIfEmpty(match.Groups["aRole"].Value), null, null, null));
        list.Add(new PartyRecord("person", match.Groups["b"].Value.Trim(),
            NullIfEmpty(match.Groups["bRole"].Value), null, null, null));
        return list;
    }

    private static readonly Regex PurposeRegex = new(
        @"for the purpose of\s+(?<value>[^\.\r\n]+?)\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PreamblePurposeRegex = new(
        @"(?<value>[^\r\n]*\bpurpose\b[^\r\n]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? ExtractPermittedPurpose(string body, string subtype)
    {
        var match = PurposeRegex.Match(body);
        if (match.Success)
        {
            var raw = match.Groups["value"].Value.Trim();
            if (!RegexHelpers.LooksLikePlaceholder(raw))
            {
                return raw;
            }
        }

        // Fallback for templates: derive from subtype.
        if (NdaSchemaV1Constants.Subtypes.Templates.Contains(subtype))
        {
            return subtype switch
            {
                NdaSchemaV1Constants.Subtypes.InvestorTemplate =>
                    "investment due diligence and partnership evaluation",
                NdaSchemaV1Constants.Subtypes.AdvisorTemplate =>
                    "advisor engagement evaluation",
                NdaSchemaV1Constants.Subtypes.DemoParticipantTemplate =>
                    "product demo participation and evaluation",
                _ => null
            };
        }

        // Last-ditch: first paragraph containing "purpose".
        var preambleMatch = PreamblePurposeRegex.Match(body);
        if (preambleMatch.Success)
        {
            var raw = preambleMatch.Groups["value"].Value.Trim();
            if (raw.Length is > 0 and < 500 && !RegexHelpers.LooksLikePlaceholder(raw))
            {
                return raw;
            }
        }
        return null;
    }

    private static string? ExtractConfidentialInformationDefinition(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var paragraphs = body.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in paragraphs)
        {
            if (p.Contains("Confidential Information", StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = p.Trim();
                return trimmed.Length > 500 ? trimmed[..500] : trimmed;
            }
        }
        return null;
    }

    private static readonly Regex ExclusionRegex = new(
        @"(?:not|without)[^\.\r\n]{0,200}?(?:public|known|received|developed|required by law)[^\.\r\n]{0,200}?\.",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static List<string> ExtractExclusions(string body)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(body)) return list;
        foreach (Match m in ExclusionRegex.Matches(body))
        {
            var text = m.Value.Trim();
            if (text.Length > 0 && !list.Contains(text)) list.Add(text);
        }
        return list;
    }

    private static readonly Regex TermRegex = new(
        @"for a period of\s+(?<n>\d+)\s+(?<unit>years|months)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static TermRecord? ExtractTerm(string body, bool isTemplate, bool isExecuted)
    {
        var match = TermRegex.Match(body);
        if (match.Success && int.TryParse(match.Groups["n"].Value, out var n))
        {
            var unit = match.Groups["unit"].Value.ToLowerInvariant();
            var months = unit.StartsWith("year") ? n * 12 : n;
            return new TermRecord("fixed_months", months, null);
        }

        // Default: 24-month industry standard for executed-looking docs only.
        if (!isTemplate && isExecuted)
        {
            return new TermRecord("fixed_months", 24, null);
        }
        // Executed-looking but no signature plumbing? still default for non-templates.
        if (!isTemplate)
        {
            return new TermRecord("fixed_months", 24, null);
        }
        return null;
    }

    private static readonly Regex NonSolicitRegex = new(
        @"non[\s-]?solicit[^\.\r\n]{0,80}?(?<n>\d+)\s*(?<unit>months|years)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static int? ExtractNonSolicitMonths(string body)
    {
        var match = NonSolicitRegex.Match(body);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups["n"].Value, out var n)) return null;
        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        return unit.StartsWith("year") ? n * 12 : n;
    }

    private static int CountMissingKeyFields(IReadOnlyDictionary<string, ExtractedField> fields)
    {
        var keyNames = new[] { "effective_date", "governing_law", "parties", "permitted_purpose" };
        var missing = 0;
        foreach (var name in keyNames)
        {
            if (!fields.TryGetValue(name, out var f) || f.Value is null) missing++;
            else if (f.Value is System.Collections.ICollection coll && coll.Count == 0) missing++;
        }
        return missing;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
