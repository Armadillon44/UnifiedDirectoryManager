using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class ReadmeWindow : Window
{
    public ReadmeWindow()
    {
        InitializeComponent();
        ReadmeText.Text = LoadReadme();
    }

    /// <summary>Reads the README embedded in the assembly (works from the self-contained exe).</summary>
    private static string LoadReadme()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("README.md", StringComparison.OrdinalIgnoreCase));
            if (name is null) return "README not found.";
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return "Could not load README: " + ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
