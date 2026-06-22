using System.Windows;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class EntraSyncWindow : Window
{
    public EntraSyncWindow()
    {
        InitializeComponent();
        this.FixLazyRender();
        PasswordBox.PasswordChanged += (_, _) =>
        {
            if (DataContext is EntraSyncViewModel vm) vm.Password = PasswordBox.Password;
        };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
