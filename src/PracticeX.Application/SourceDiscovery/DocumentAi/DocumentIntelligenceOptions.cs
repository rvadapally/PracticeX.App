namespace PracticeX.Application.SourceDiscovery.DocumentAi;

/// <summary>
/// Configuration for the Azure Document Intelligence provider. The defaults
/// keep the provider OFF so a fresh checkout never accidentally bills against
/// an Azure resource. Operators must explicitly set <see cref="Enabled"/>=true
/// AND list the tenant in <see cref="AllowedTenantIds"/> before any document
/// content leaves the host.
/// </summary>
public sealed class DocumentIntelligenceOptions
{
    public const string SectionName = "DocumentIntelligence";

    /// <summary>Master kill-switch. False = NoOp provider is registered.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Azure resource endpoint, e.g. https://practicex-docintel.cognitiveservices.azure.com/.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>API key. Read from user-secrets or environment, NEVER appsettings.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Azure region. Must be HIPAA-eligible (eastus, eastus2, westus2, southcentralus, etc).</summary>
    public string Region { get; set; } = "eastus";

    /// <summary>Default layout model. prebuilt-layout returns text + tables + selection marks.</summary>
    public string LayoutModelId { get; set; } = "prebuilt-layout";

    /// <summary>Default contract model. prebuilt-contract is Microsoft's contract-specific model.</summary>
    public string ContractModelId { get; set; } = "prebuilt-contract";

    /// <summary>
    /// Tenant allowlist. A tenant must appear here before any of its documents
    /// flow to Azure, regardless of <see cref="Enabled"/>. Empty list = no tenants
    /// allowed. Required gate before BAA-confirmed customer data is processed.
    /// </summary>
    public List<Guid> AllowedTenantIds { get; set; } = new();

    /// <summary>Cap on pages per document. Hard guardrail against runaway cost.</summary>
    public int MaxPagesPerDocument { get; set; } = 200;

    /// <summary>Per-call timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}
