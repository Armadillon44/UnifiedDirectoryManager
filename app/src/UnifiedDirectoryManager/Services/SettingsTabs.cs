namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Tab headers in the Settings dialog, as constants rather than loose strings.
/// </summary>
/// <remarks>
/// A caller asks for a tab by the words on it, which is fragile the moment anyone renames one. Naming
/// them here does not make that safe by itself — so the suite checks every constant against the headers
/// actually in <c>SettingsWindow.xaml</c>, and a rename that forgets this file fails by name rather than
/// silently opening the dialog on the wrong page.
/// </remarks>
public static class SettingsTabs
{
    public const string OnPremAd = "On-prem AD (LDAP)";
    public const string Cloud = "Cloud (Entra ID)";
    public const string EntraConnect = "Entra Connect";
    public const string Logs = "Logs";

    /// <summary>How patiently to wait for Entra ID and Exchange Online after an on-prem create.</summary>
    public const string Retries = "Retries";
    public const string Toolbar = "Toolbar";

    public static IReadOnlyList<string> All { get; } = new[] { OnPremAd, Cloud, EntraConnect, Logs, Retries, Toolbar };
}
