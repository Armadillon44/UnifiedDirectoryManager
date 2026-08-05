using System.ComponentModel;
using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class DistributionGroupMembersWindow : Window
{
    public DistributionGroupMembersWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is DistributionGroupMembersViewModel vm) _ = vm.LoadAsync();
    }

    private void OnRemoveSelected(object sender, RoutedEventArgs e)
    {
        // The command needs the ListView's live selection, which only the view has. Execute() does NOT consult
        // CanExecute, so the gate has to be checked here or a second batch can start on top of a running one.
        if (DataContext is not DistributionGroupMembersViewModel vm) return;
        if (vm.RemoveMembersCommand.CanExecute(MembersList.SelectedItems))
            vm.RemoveMembersCommand.Execute(MembersList.SelectedItems);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Only a WRITE batch blocks the close. It is one Exchange round trip per member and isn't cancellable,
        // so closing mid-run would leave the rest running against a dead view model and the operator with no
        // report of what landed. A plain read has nothing to lose, and blocking on it would strand the operator
        // behind a modal for as long as the read takes.
        if (DataContext is DistributionGroupMembersViewModel { IsWriting: true } vm)
        {
            e.Cancel = true;
            vm.NoteCloseBlocked();
        }
        base.OnClosing(e);
    }
}
