using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class CopyToTemplateWindow : Window
{
    public CopyToTemplateWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
        Loaded += async (_, _) =>
        {
            if (DataContext is CopyToTemplateViewModel vm) await vm.LoadAsync();
        };
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CopyToTemplateViewModel vm) return;
        vm.SaveCommand.Execute(null);
        if (vm.Saved) Close(); // a one-shot save — the success alert confirms it
    }
}
