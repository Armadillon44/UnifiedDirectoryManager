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

    /// <summary>
    /// Pinned favourites, keyed by the LOWER-CASED domain FQDN they belong to. Per domain because a
    /// distinguished name only means something in the domain it came from — one global list would show
    /// entries that fail the moment they are clicked. Use <see cref="Favorites"/> rather than this directly:
    /// the key has to be normalised, because the JSON deserialiser rebuilds this with a default comparer.
    /// </summary>
    public Dictionary<string, List<Models.FavoriteEntry>> Favorites { get; set; } = new();

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

    // --- How patiently to wait for the cloud to catch up after an on-prem create ---
    // Separate for the two services because their per-attempt cost differs by roughly ninety times: a
    // Graph failure returns in well under a second, while an Exchange call that hangs costs the full
    // 90-second operation budget before the wait even starts. Zero means “never set” and clamps up to
    // the minimum — see RetryPolicy.Clamped.

    /// <summary>Attempts when Entra (Graph) says the object is not visible yet.</summary>
    public int EntraRetryAttempts { get; set; }

    /// <summary>Seconds between those attempts.</summary>
    public int EntraRetryWaitSeconds { get; set; }

    /// <summary>Attempts when Exchange Online has not provisioned the recipient yet.</summary>
    public int ExchangeRetryAttempts { get; set; }

    /// <summary>Seconds between those attempts.</summary>
    public int ExchangeRetryWaitSeconds { get; set; }

    /// <summary>The two policies, with anything out of range clamped into it.</summary>
    public RetryPolicy EntraRetry => new RetryPolicy(EntraRetryAttempts, EntraRetryWaitSeconds).Clamped();
    public RetryPolicy ExchangeRetry => new RetryPolicy(ExchangeRetryAttempts, ExchangeRetryWaitSeconds).Clamped();

    /// <summary>
    /// Ids of the toolbar items the operator has chosen, in order (audit T1). Empty or absent means the
    /// default set, so an existing settings file needs no migration and a new install looks as it did.
    /// Ids this build does not recognise are dropped on load rather than rejected — see
    /// <see cref="ToolbarCatalogue.Normalise"/>.
    /// </summary>
    public List<string> ToolbarItemIds { get; set; } = new();

    /// <summary>Visible-column keys for the Exchange Online Mailboxes / Distribution Groups lists.</summary>
    public List<string> VisibleExchangeMailboxColumns { get; set; } = new();
    public List<string> VisibleExchangeGroupColumns { get; set; } = new();
}
