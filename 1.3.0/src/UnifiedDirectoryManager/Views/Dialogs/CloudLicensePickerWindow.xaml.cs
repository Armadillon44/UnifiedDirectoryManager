using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class CloudLicensePickerWindow : Window
{
    public CloudLicensePickerWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (DataContext is CloudLicensePickerViewModel vm) vm.Commit(SkuList.SelectedItems);
        DialogResult = true;
        Close();
    }
}
