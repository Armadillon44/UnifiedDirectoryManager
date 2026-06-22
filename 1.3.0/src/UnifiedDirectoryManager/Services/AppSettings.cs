namespace UnifiedDirectoryManager.Services;

/// <summary>
/// User preferences persisted between sessions: the last connection (no password — that lives in
/// Credential Manager) and window/pane layout.
/// </summary>
public sealed class AppSettings
{
    // --- Last connection (for default-to-last-DC) ---
    public string? LastDomainFqdn { get; set; }
    public string? LastPrimaryDc { get; set; }
    public List<string> LastFallbackDcs { get; set; } = new();
    public bool LastUseLdaps { get; set; }
    public string? LastUsername { get; set; }

    // --- View layout ---
    public string EditDock { get; set; } = "Right"; // EditPaneDock
    public double TreeWidth { get; set; } = 300;
    public double EditPaneWidth { get; set; } = 440;
    public double EditPaneHeight { get; set; } = 320;
    public double WindowWidth { get; set; } = 1240;
    public double WindowHeight { get; set; } = 760;
    public bool WindowMaximized { get; set; }

    /// <summary>lDAPDisplayNames of the visible object-list columns (empty = use defaults).</summary>
    public List<string> VisibleColumns { get; set; } = new();

    /// <summary>Last Entra Connect server used for a remote delta sync.</summary>
    public string? EntraConnectServer { get; set; }

    /// <summary>
    /// Folder where operation logs (e.g. scenario-run logs of the steps taken and changes made) are
    /// saved. Empty/null = the default (%APPDATA%\UnifiedDirectoryManager\OperationLogs).
    /// </summary>
    public string? OperationLogDirectory { get; set; }

    // --- Entra ID / Microsoft Graph (cloud reads) ---
    // Public-client app-registration identifiers; not secrets (PKCE is used). Tokens live in the
    // DPAPI-backed MSAL cache, not here.
    public string? EntraTenantId { get; set; }
    public string? EntraClientId { get; set; }

    /// <summary>Visible-column keys for the cloud Users / Groups / Devices lists (empty = use defaults).</summary>
    public List<string> VisibleCloudUserColumns { get; set; } = new();
    public List<string> VisibleCloudGroupColumns { get; set; } = new();
    public List<string> VisibleCloudDeviceColumns { get; set; } = new();
}
