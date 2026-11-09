using System.Windows;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class ObjectEditorWindow : Window
{
    public ObjectEditorWindow()
    {
        InitializeComponent();
        // Closing over unsaved edits asks first: save, discard, or stay.
        UnsavedChangesGuard.Attach(this);
    }
}
