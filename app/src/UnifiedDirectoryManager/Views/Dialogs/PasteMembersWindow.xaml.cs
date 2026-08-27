using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

/// <summary>
/// Paste a list of people, review what each line resolved to, and hand the settled ones back. It never
/// writes: the membership editor that opened it owns the add, so there is one write path and one report.
/// </summary>
public partial class PasteMembersWindow : Window
{
    public PasteMembersWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PasteMembersViewModel vm) return;

        // Nothing settled means nothing to do. Closing with OK here would have the editor report "0 added",
        // which reads as a failure rather than as "you have not chosen anybody yet".
        if (!vm.Commit())
        {
            vm.Status = "No rows are ready — resolve the list, and choose a match for anything ambiguous.";
            return;
        }
        DialogResult = true;
        Close();
    }
}
