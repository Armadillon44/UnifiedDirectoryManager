namespace UnifiedDirectoryManager.Models;

/// <summary>
/// A row parsed from an import CSV, used to seed an editable grid row. Best-effort resolution (manager,
/// cloud groups) happens at import time; anything that couldn't be mapped/resolved is reported in
/// <see cref="Warnings"/> rather than silently dropped.
/// </summary>
public sealed class ImportedUserRow
{
    public string FirstName { get; set; } = string.Empty;
    public string MiddleName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Initials { get; set; } = string.Empty;
    public string SamOverride { get; set; } = string.Empty;
    public string Upn { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>Resolved manager DN (null when none / unresolved).</summary>
    public string? ManagerDn { get; set; }
    public string ManagerDisplay { get; set; } = string.Empty;

    /// <summary>Resolved Entra ID groups for this row.</summary>
    public List<CloudGroupRef> CloudGroups { get; } = new();

    public bool IssueTap { get; set; }

    /// <summary>Extra attribute overrides keyed by lDAPDisplayName (from any recognized attribute column).</summary>
    public Dictionary<string, string> AttributeOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Warnings { get; } = new();
}

/// <summary>Batch-wide Entra Connect sync settings used by the cloud phase (sync → wait → groups/TAP).</summary>
public sealed class BulkCloudOptions
{
    public string EntraConnectServer { get; set; } = string.Empty;
    public bool SpecifyCredentials { get; set; }
    public string Username { get; set; } = string.Empty;
    /// <summary>Sync-account password — held only for the run, never persisted.</summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Immutable per-user snapshot the bulk engine creates. Attributes/proxies are already resolved via the
/// shared <c>UserAttributeBuilder</c>, so the engine just generates the passphrase and creates the account.
/// </summary>
public sealed class BulkCreateRequest
{
    public required string Label { get; init; }
    public required string TargetOu { get; init; }
    public required IReadOnlyDictionary<string, string> Attributes { get; init; }
    public IReadOnlyList<string> Proxies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> OnPremGroupDns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CloudGroupRef> CloudGroups { get; init; } = Array.Empty<CloudGroupRef>();
    public bool IssueTap { get; init; }
    public int TapLifetimeMinutes { get; init; } = 1440;
    public bool TapOneTimeUse { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>Force a password change at next logon for the created account (set batch-wide; off by default).</summary>
    public bool MustChangePassword { get; init; }

    /// <summary>The routable UPN used to correlate the synced user in Entra (for cloud groups / TAP).</summary>
    public string? Upn { get; init; }

    public bool NeedsCloud => CloudGroups.Count > 0 || IssueTap;
}

/// <summary>Per-user outcome of a bulk create, including the generated passphrase and TAP (shown once, in the report).</summary>
public sealed class BulkCreateUserResult
{
    public required string Label { get; init; }
    public string SamAccountName { get; init; } = string.Empty;
    public bool Success { get; set; }
    /// <summary>Display text for the result column.</summary>
    public string ResultText => Success ? "Created" : "Failed";
    public string? Error { get; set; }
    public string? DistinguishedName { get; set; }

    /// <summary>The generated passphrase (secret — surfaced only in the post-run report, never logged).</summary>
    public string GeneratedPassword { get; set; } = string.Empty;
    public bool PasswordSet { get; set; }

    /// <summary>The issued Temporary Access Pass, if any (secret — shown once in the report).</summary>
    public string? TapCode { get; set; }

    /// <summary>Human-readable summary of the cloud (groups/TAP) outcome for this user.</summary>
    public string CloudSummary { get; set; } = string.Empty;

    /// <summary>Engine bookkeeping: true once the cloud phase finished for this user (so a retry skips it).</summary>
    public bool CloudApplied { get; set; }
}

/// <summary>Aggregate result of a bulk create run.</summary>
public sealed class BulkCreateReport
{
    public required IReadOnlyList<BulkCreateUserResult> Items { get; init; }
    public int SuccessCount => Items.Count(i => i.Success);
    public int FailureCount => Items.Count(i => !i.Success);
}
