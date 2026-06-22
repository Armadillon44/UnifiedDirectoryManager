using System.IO;
using System.Windows;

namespace UnifiedDirectoryManager.Views;

/// <summary>
/// Copies a sensitive value (e.g. a freshly generated password) to the clipboard while tagging the payload
/// with the Windows registered formats that keep it OUT of Clipboard History (Win+V) and the Cloud Clipboard
/// sync to the user's other devices. See
/// https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats#cloud-clipboard-and-clipboard-history-formats
/// </summary>
internal static class SensitiveClipboard
{
    public static void SetText(string text)
    {
        var data = new DataObject();
        data.SetText(text);

        // The mere presence of this format excludes ALL formats from both history and cloud sync.
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 0 }));
        // Belt-and-suspenders: explicit serialized DWORD(0) opt-outs per channel (4 zero bytes each).
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));

        Clipboard.SetDataObject(data, copy: true);
    }
}
