namespace UnifiedDirectoryManager.Models;

/// <summary>The kind of action a single scenario step performs against each target object.</summary>
public enum ScenarioActionType
{
    /// <summary>Disable the account (set the ACCOUNTDISABLE flag).</summary>
    Disable,
    /// <summary>Enable the account (clear the ACCOUNTDISABLE flag).</summary>
    Enable,
    /// <summary>Unlock a locked-out account.</summary>
    Unlock,
    /// <summary>Remove the object from every group it currently belongs to.</summary>
    RemoveAllGroups,
    /// <summary>Add the object to the listed groups.</summary>
    AddToGroups,
    /// <summary>Remove the object from the listed groups.</summary>
    RemoveFromGroups,
    /// <summary>Set an attribute to a (token-supported) value; a blank value clears it.</summary>
    SetAttribute,
    /// <summary>Clear an attribute entirely.</summary>
    ClearAttribute,
    /// <summary>Set the description attribute to a (token-supported) note.</summary>
    SetDescription,
    /// <summary>Move the object to another OU/container.</summary>
    MoveToOu,

    // --- Cloud (Entra ID) actions; act on the object's synced cloud twin. Require Entra sign-in. ---

    /// <summary>Disable the user's Entra ID (cloud) account.</summary>
    CloudDisableAccount,
    /// <summary>Enable the user's Entra ID (cloud) account.</summary>
    CloudEnableAccount,
    /// <summary>Revoke the user's Entra ID sign-in sessions (invalidate refresh tokens).</summary>
    CloudRevokeSessions,
    /// <summary>Add the object's cloud twin to the listed Entra ID groups.</summary>
    CloudAddToGroups,
    /// <summary>Remove the object's cloud twin from the listed Entra ID groups.</summary>
    CloudRemoveFromGroups,
    /// <summary>Remove the object's cloud twin from every Entra ID group it belongs to (skips dynamic + on-prem-synced).</summary>
    CloudRemoveAllGroups,

    /// <summary>
    /// Save a plain-text operation log of every step taken and change made during this run (e.g. the
    /// groups removed by a Terminate-User scenario). A meta-step: it performs no directory change. The
    /// log goes to the folder set in Settings ▸ Logs; the operator is reminded of the path and can
    /// override it when the scenario runs.
    /// </summary>
    SaveOperationLog,
}

/// <summary>A reference to an Entra ID group stored in a scenario step (id + display name).</summary>
public sealed class CloudGroupRef
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// One step in a scenario. Only the fields relevant to <see cref="Action"/> are used; the rest are
/// ignored (and left at their defaults when serialized).
/// </summary>
public sealed class ScenarioStep
{
    public ScenarioActionType Action { get; set; }

    /// <summary>lDAPDisplayName for SetAttribute / ClearAttribute.</summary>
    public string Attribute { get; set; } = string.Empty;

    /// <summary>Value for SetAttribute / SetDescription (supports {date} {datetime} {time} {admin} {name} tokens).</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Group DNs for AddToGroups / RemoveFromGroups.</summary>
    public List<string> GroupDns { get; set; } = new();

    /// <summary>Entra ID groups for CloudAddToGroups / CloudRemoveFromGroups.</summary>
    public List<CloudGroupRef> CloudGroups { get; set; } = new();

    /// <summary>Target OU/container DN for MoveToOu.</summary>
    public string TargetOu { get; set; } = string.Empty;
}

/// <summary>
/// A named, reusable sequence of actions run against one or many selected objects (e.g. "Terminate
/// User"). Steps execute in order, per object. Persisted as JSON like new-user templates.
/// </summary>
public sealed class Scenario
{
    public string Name { get; set; } = "New scenario";
    public string Description { get; set; } = string.Empty;
    public List<ScenarioStep> Steps { get; set; } = new();
}
