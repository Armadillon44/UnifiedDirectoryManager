using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class OuPropertiesWindow : Window
{
    public OuPropertiesWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is OuPropertiesViewModel vm) await vm.LoadAsync();
        };
    }
}
