using System.Windows.Controls;

namespace UnifiedDirectoryManager.Views.Controls;

/// <summary>
/// Exchange Online mailbox actions, bound to an <see cref="ViewModels.ExchangeTabViewModel"/>. Hosted by both
/// the AD edit pane and the cloud mailbox pane so the two cannot drift apart.
/// </summary>
public partial class ExchangeActionsView : UserControl
{
    public ExchangeActionsView() => InitializeComponent();
}
