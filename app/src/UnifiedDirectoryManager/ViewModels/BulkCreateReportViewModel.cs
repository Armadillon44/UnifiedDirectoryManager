using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Read-only post-run report for a bulk create. Surfaces every user's outcome including the generated
/// passphrase and any Temporary Access Pass — these are secrets shown <b>once</b> here (never logged), so
/// the window offers Copy and an Export-to-CSV (the export deliberately contains the plaintext passwords).
/// </summary>
public sealed class BulkCreateReportViewModel
{
    public IReadOnlyList<BulkCreateUserResult> Items { get; }
    public string Summary { get; }

    public BulkCreateReportViewModel(BulkCreateReport report)
    {
        Items = report.Items;
        Summary = $"{report.SuccessCount} created, {report.FailureCount} failed.";
    }

    /// <summary>Builds the export CSV (includes plaintext passwords / TAP codes) using the shared safe encoder.</summary>
    public string BuildCsv()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(CsvText.Row(new[]
        {
            "User", "Logon name", "Result", "Distinguished name", "Password", "Password set",
            "Temporary Access Pass", "Cloud result", "Error",
        }));
        foreach (var i in Items)
            sb.AppendLine(CsvText.Row(new[]
            {
                i.Label, i.SamAccountName, i.Success ? "Created" : "Failed", i.DistinguishedName ?? string.Empty,
                i.GeneratedPassword, i.PasswordSet ? "Yes" : "No", i.TapCode ?? string.Empty,
                i.CloudSummary, i.Error ?? string.Empty,
            }));
        return sb.ToString();
    }
}
