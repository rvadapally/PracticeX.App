using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PracticeX.Application.Common;
using PracticeX.Domain.Documents;
using PracticeX.Infrastructure.Persistence;
using PracticeX.Infrastructure.Tenancy;

namespace PracticeX.Api.Analysis;

/// <summary>
/// Slice 17 - Entity Graph. Walks every doc's canonical-headline JSON and
/// builds an Obsidian-style force-directed graph of People, Organizations,
/// Premises, and the Documents that connect them. The frontend renders
/// nodes+links with vis-network.
/// </summary>
public static class EntityGraphEndpoint
{
    public static IEndpointRouteBuilder MapEntityGraphEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/analysis").WithTags("Analysis");
        group.MapGet("/entity-graph", GetEntityGraph).WithName("GetEntityGraph");
        return routes;
    }

    private static async Task<Ok<EntityGraphResponse>> GetEntityGraph(
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var tenantId = userContext.TenantId;

        // Slice 21 RBAC: graph nodes/edges respect facility scope.
        var visibleAssetIds = db.DocumentCandidates
            .Where(c => c.TenantId == tenantId)
            .ApplyFacilityScope(userContext)
            .Select(c => c.DocumentAssetId);
        var assets = await db.DocumentAssets
            .Where(a => a.TenantId == tenantId && a.LlmExtractedFieldsJson != null
                     && visibleAssetIds.Contains(a.Id))
            .ToListAsync(cancellationToken);

        var sourceNames = await db.SourceObjects
            .Where(s => s.TenantId == tenantId)
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        var candidatesByAsset = await db.DocumentCandidates
            .Where(c => c.TenantId == tenantId)
            .ToDictionaryAsync(c => c.DocumentAssetId, c => c.CandidateType, cancellationToken);

        var builder = new GraphBuilder();

        foreach (var asset in assets)
        {
            var fileName = (asset.SourceObjectId.HasValue && sourceNames.TryGetValue(asset.SourceObjectId.Value, out var n))
                ? n : "(unnamed)";
            var candidateType = candidatesByAsset.GetValueOrDefault(asset.Id, "unknown");
            var family = MapFamily(candidateType);

            // Always add the doc as a node so it can be linked.
            var docNodeId = $"doc:{asset.Id}";
            builder.AddDocument(docNodeId, fileName, family, asset.Id);

            JsonElement? root = null;
            try
            {
                using var doc = JsonDocument.Parse(asset.LlmExtractedFieldsJson!);
                root = doc.RootElement.Clone();
            }
            catch { /* skip malformed */ }
            if (root is not JsonElement r) continue;

            // Headline-driven entities (Slice 18 canonical fields).
            if (r.TryGetProperty("headline", out var hl) && hl.ValueKind == JsonValueKind.Object)
            {
                AbsorbHeadline(hl, family, docNodeId, builder);
            }

            // Top-level lease fields (extracted JSON also surfaces these).
            AbsorbTopLevel(r, docNodeId, builder);

            // Parties array (NDA / employment / call coverage).
            if (r.TryGetProperty("parties", out var parties) && parties.ValueKind == JsonValueKind.Array)
            {
                foreach (var party in parties.EnumerateArray())
                {
                    if (party.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                    {
                        var name = nm.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            var role = party.TryGetProperty("role", out var rl) && rl.ValueKind == JsonValueKind.String
                                ? rl.GetString() : null;
                            var type = InferOrgVsPerson(name!);
                            var nodeId = builder.AddEntity(name!, type);
                            builder.AddLink(docNodeId, nodeId, role ?? "party");
                        }
                    }
                }
            }

            // Premises array (lease).
            if (r.TryGetProperty("premises", out var premises) && premises.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in premises.EnumerateArray())
                {
                    var street = p.TryGetProperty("street_address", out var stEl) &&
                                 stEl.ValueKind == JsonValueKind.String ? stEl.GetString() : null;
                    var suite = p.TryGetProperty("suite", out var suEl) &&
                                suEl.ValueKind == JsonValueKind.String ? suEl.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(street))
                    {
                        var label = string.IsNullOrWhiteSpace(suite) ? street! : $"{street} (Suite {suite})";
                        var nodeId = builder.AddEntity(label, "asset");
                        builder.AddLink(docNodeId, nodeId, "premises");
                    }
                }
            }
        }

        var (nodes, links) = builder.Build();
        return TypedResults.Ok(new EntityGraphResponse(nodes, links));
    }

    private static void AbsorbHeadline(JsonElement headline, string family, string docNodeId, GraphBuilder builder)
    {
        // Lease.
        var landlord = StringField(headline, "landlord");
        var tenant = StringField(headline, "tenant");
        var premises = StringField(headline, "premises_address");
        if (landlord != null)
        {
            var n = builder.AddEntity(landlord, InferOrgVsPerson(landlord));
            builder.AddLink(docNodeId, n, "landlord");
        }
        if (tenant != null)
        {
            var n = builder.AddEntity(tenant, InferOrgVsPerson(tenant));
            builder.AddLink(docNodeId, n, "tenant");
        }
        if (premises != null)
        {
            var n = builder.AddEntity(premises, "asset");
            builder.AddLink(docNodeId, n, "premises");
        }

        // NDA.
        var counterparty = StringField(headline, "counterparty_name");
        if (counterparty != null)
        {
            var n = builder.AddEntity(counterparty, InferOrgVsPerson(counterparty));
            builder.AddLink(docNodeId, n, "counterparty");
        }

        // Employment.
        var employer = StringField(headline, "employer");
        var physician = StringField(headline, "physician_name");
        if (employer != null)
        {
            var n = builder.AddEntity(employer, "organization");
            builder.AddLink(docNodeId, n, "employer");
        }
        if (physician != null)
        {
            var n = builder.AddEntity(physician, "person");
            builder.AddLink(docNodeId, n, "physician");
        }

        // Call coverage.
        var coveredFacility = StringField(headline, "covered_facility");
        var coveringGroup = StringField(headline, "covering_group");
        if (coveredFacility != null)
        {
            var n = builder.AddEntity(coveredFacility, "asset");
            builder.AddLink(docNodeId, n, "covered_facility");
        }
        if (coveringGroup != null)
        {
            var n = builder.AddEntity(coveringGroup, "organization");
            builder.AddLink(docNodeId, n, "covering_group");
        }
    }

    private static void AbsorbTopLevel(JsonElement root, string docNodeId, GraphBuilder builder)
    {
        // Some Stage-2 prompts emit landlord/tenant at the top level too.
        var landlord = StringField(root, "landlord");
        var tenant = StringField(root, "tenant");
        if (landlord != null)
        {
            var n = builder.AddEntity(landlord, InferOrgVsPerson(landlord));
            builder.AddLink(docNodeId, n, "landlord");
        }
        if (tenant != null)
        {
            var n = builder.AddEntity(tenant, InferOrgVsPerson(tenant));
            builder.AddLink(docNodeId, n, "tenant");
        }
    }

    private static string? StringField(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var s = el.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>
    /// Best-effort heuristic. Person names are 2-4 words, no entity-suffix
    /// markers, optionally with M.D./Ph.D. Anything with corp suffixes or
    /// 5+ words tilts to organization.
    /// </summary>
    private static string InferOrgVsPerson(string name)
    {
        var lower = name.ToLowerInvariant();
        var orgMarkers = new[]
        {
            " llc", " inc", " corp", " corporation", " p.a.", " pa", " pllc",
            " l.l.c.", " l.l.p.", " ltd", " hospital", " health", " medical",
            " physicians", " associates", " group", " center", " clinic",
            " university", " school", " baptist", "limited"
        };
        foreach (var m in orgMarkers)
        {
            if (lower.Contains(m)) return "organization";
        }
        var wordCount = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount >= 5) return "organization";
        // Person-y suffixes
        if (lower.EndsWith(" m.d.") || lower.EndsWith(", m.d.") || lower.EndsWith(" md") ||
            lower.EndsWith(" ph.d.") || lower.EndsWith(" phd") || lower.EndsWith(" do"))
        {
            return "person";
        }
        return wordCount <= 4 ? "person" : "organization";
    }

    private static string MapFamily(string candidateType) => candidateType switch
    {
        DocumentCandidateTypes.Lease or
        DocumentCandidateTypes.LeaseAmendment or
        DocumentCandidateTypes.LeaseLoi => "lease",

        DocumentCandidateTypes.EmployeeAgreement or
        DocumentCandidateTypes.Amendment => "employment_governance",

        DocumentCandidateTypes.Nda => "nda",
        DocumentCandidateTypes.CallCoverageAgreement => "scheduling",
        DocumentCandidateTypes.Bylaws => "governance",
        DocumentCandidateTypes.ServiceAgreement or
        DocumentCandidateTypes.VendorContract => "vendor_services",
        _ => "other"
    };

    /// <summary>
    /// Builds the graph in a single pass with key-normalized entity dedupe and
    /// edge dedupe. Same shape EntityRegistry uses in AnalysisEndpoints.cs but
    /// we also keep entity-type so the UI can color-code nodes.
    /// </summary>
    private sealed class GraphBuilder
    {
        // entity key -> (display label, type, link count)
        private readonly Dictionary<string, EntityState> _entities = new(StringComparer.Ordinal);
        // doc node id -> doc display label / family / asset id
        private readonly Dictionary<string, DocState> _docs = new(StringComparer.Ordinal);
        // dedupe links by (source, target, relation)
        private readonly HashSet<string> _linkKeys = new(StringComparer.Ordinal);
        private readonly List<RawLink> _links = new();

        public string AddEntity(string display, string type)
        {
            var key = NormalizeKey(display);
            if (string.IsNullOrEmpty(key)) key = display.ToLowerInvariant();
            var nodeId = $"ent:{type}:{key}";

            if (!_entities.TryGetValue(nodeId, out var state))
            {
                state = new EntityState(display.Trim(), type, 0);
                _entities[nodeId] = state;
            }
            else if (display.Length > state.Display.Length)
            {
                _entities[nodeId] = state with { Display = display.Trim() };
            }
            return nodeId;
        }

        public void AddDocument(string nodeId, string display, string family, Guid documentAssetId)
        {
            _docs[nodeId] = new DocState(display, family, documentAssetId);
        }

        public void AddLink(string source, string target, string relation)
        {
            if (source == target) return;
            var key = $"{source}|{target}|{relation}";
            if (!_linkKeys.Add(key)) return;
            _links.Add(new RawLink(source, target, relation));
            // Bump entity link count for sizing.
            if (_entities.TryGetValue(target, out var s))
            {
                _entities[target] = s with { LinkCount = s.LinkCount + 1 };
            }
        }

        public (List<EntityGraphNode> nodes, List<EntityGraphLink> links) Build()
        {
            var nodes = new List<EntityGraphNode>(_entities.Count + _docs.Count);
            foreach (var (id, ent) in _entities)
            {
                nodes.Add(new EntityGraphNode(
                    Id: id,
                    Label: ent.Display,
                    Type: ent.Type,
                    Family: null,
                    DocumentAssetId: null,
                    Size: 8 + Math.Min(20, ent.LinkCount * 2)));
            }
            foreach (var (id, doc) in _docs)
            {
                nodes.Add(new EntityGraphNode(
                    Id: id,
                    Label: doc.Display,
                    Type: "document",
                    Family: doc.Family,
                    DocumentAssetId: doc.DocumentAssetId,
                    Size: 6));
            }

            var links = _links.Select(l => new EntityGraphLink(
                Source: l.Source,
                Target: l.Target,
                Relation: l.Relation,
                DocumentAssetId: _docs.TryGetValue(l.Source, out var d) ? d.DocumentAssetId :
                                 _docs.TryGetValue(l.Target, out var d2) ? d2.DocumentAssetId : null,
                Inferred: false
            )).ToList();

            // Inferred co-appearance edges: when two entities of the same type
            // both touch the same document, surface a direct edge between them.
            // Lets the UI show person-person clustering when the user filters
            // out the "document" chip — otherwise people would orphan because
            // every relationship in v1 routes through a document hub.
            // Cap per-doc fan-out at 6 to keep board-bylaws docs from emitting
            // 30+ noisy pairs.
            var entitiesByDoc = new Dictionary<string, List<(string id, string type)>>();
            foreach (var l in _links)
            {
                if (_docs.ContainsKey(l.Source) && _entities.TryGetValue(l.Target, out var e))
                {
                    if (!entitiesByDoc.TryGetValue(l.Source, out var list))
                    {
                        list = new();
                        entitiesByDoc[l.Source] = list;
                    }
                    list.Add((l.Target, e.Type));
                }
            }

            var inferredKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (docId, members) in entitiesByDoc)
            {
                if (members.Count > 6) continue;
                var docLabel = _docs.TryGetValue(docId, out var ds) ? ds.Display : docId;
                var docAssetId = _docs.TryGetValue(docId, out var ds2) ? (Guid?)ds2.DocumentAssetId : null;
                for (var i = 0; i < members.Count; i++)
                {
                    for (var j = i + 1; j < members.Count; j++)
                    {
                        if (members[i].type != members[j].type) continue;
                        if (members[i].type == "document") continue;
                        var a = members[i].id;
                        var b = members[j].id;
                        // Canonical-ordered key so (a,b) == (b,a).
                        var key = string.CompareOrdinal(a, b) < 0 ? $"{a}|{b}" : $"{b}|{a}";
                        if (!inferredKeys.Add(key)) continue;
                        links.Add(new EntityGraphLink(
                            Source: a,
                            Target: b,
                            Relation: $"co-appears in {Truncate(docLabel, 40)}",
                            DocumentAssetId: docAssetId,
                            Inferred: true
                        ));
                    }
                }
            }

            return (nodes, links);
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";

        private static readonly string[] EntitySuffixes =
        {
            "p a", "pa", "pllc", "llc", "lp", "llp", "inc", "incorporated",
            "corp", "corporation", "co", "ltd", "limited", "the", "md", "phd"
        };

        private static string NormalizeKey(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '&' || ch == '/' || ch == '.' || ch == ',')
                    sb.Append(' ');
            }
            var collapsed = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            var tokens = collapsed.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t != "and")
                .ToList();
            // Strip trailing entity-suffix tokens. Loop because "phd llc" can stack.
            bool changed = true;
            while (changed && tokens.Count > 0)
            {
                changed = false;
                if (EntitySuffixes.Contains(tokens[^1]))
                {
                    tokens.RemoveAt(tokens.Count - 1);
                    changed = true;
                    continue;
                }
                // "p a" / "m d" / "d o" two-token suffixes (degree/business marks).
                if (tokens.Count >= 2)
                {
                    var two = tokens[^2] + " " + tokens[^1];
                    if (two is "p a" or "m d" or "d o" or "ph d")
                    {
                        tokens.RemoveAt(tokens.Count - 1);
                        tokens.RemoveAt(tokens.Count - 1);
                        changed = true;
                    }
                }
            }
            return string.Join(' ', tokens);
        }

        private sealed record EntityState(string Display, string Type, int LinkCount);
        private sealed record DocState(string Display, string Family, Guid DocumentAssetId);
        private sealed record RawLink(string Source, string Target, string Relation);
    }
}

public sealed record EntityGraphResponse(
    IReadOnlyList<EntityGraphNode> Nodes,
    IReadOnlyList<EntityGraphLink> Links);

public sealed record EntityGraphNode(
    string Id,
    string Label,
    string Type,            // "person" | "organization" | "asset" | "document"
    string? Family,         // doc family when type=="document"
    Guid? DocumentAssetId,  // populated for type=="document"
    int Size);

public sealed record EntityGraphLink(
    string Source,
    string Target,
    string Relation,
    Guid? DocumentAssetId,
    bool Inferred);
