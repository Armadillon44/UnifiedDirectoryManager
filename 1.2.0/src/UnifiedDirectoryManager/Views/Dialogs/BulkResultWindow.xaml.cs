using System.Windows;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class BulkResultWindow : Window
{
    public BulkResultWindow(BulkResult result)
    {
        InitializeComponent();
        Summary.Text = $"{result.SuccessCount} succeeded, {result.FailureCount} failed.";
        Items.ItemsSource = result.Items
            .Select(i => new { i.Name, Outcome = i.Success ? "OK" : "Failed", Detail = i.Error ?? string.Empty })
            .ToList();
    }
}
