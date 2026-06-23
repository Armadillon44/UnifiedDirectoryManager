using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        this.FixLazyRender(); // without this the window can render blank until it's invalidated (resized/occluded)
        // Show Major.Minor.Build (e.g. "1.1.1") from the assembly version set in the .csproj.
        var asmVersion = Assembly.GetExecutingAssembly().GetName().Version;
        var version = asmVersion is not null ? asmVersion.ToString(3) : "1.1.1";
        VersionText.Text = $"Version {version}  •  .NET {Environment.Version}";
        RuntimeText.Text = $"Architecture: {RuntimeInformation.ProcessArchitecture}  •  {RuntimeInformation.OSDescription}";
        LogPathText.Text = string.IsNullOrEmpty(AppLog.LogDirectory) ? "(none)" : AppLog.LogDirectory;
    }
}
