using CommunityToolkit.Mvvm.ComponentModel;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Backs the "change delegate access" dialog: tick which permissions (Full Access / Send As / Send on Behalf)
/// a delegate should hold, pre-checked to their current access. Auto-map applies to a newly-granted Full Access.
/// </summary>
public partial class DelegateAccessViewModel : ObservableObject
{
    public string DelegateName { get; }

    [ObservableProperty] private bool _fullAccess;
    [ObservableProperty] private bool _sendAs;
    [ObservableProperty] private bool _sendOnBehalf;
    [ObservableProperty] private bool _autoMapping = true;

    public DelegateAccessViewModel(string delegateName, DelegateAccess current)
    {
        DelegateName = delegateName;
        _fullAccess = current.HasFlag(DelegateAccess.FullAccess);
        _sendAs = current.HasFlag(DelegateAccess.SendAs);
        _sendOnBehalf = current.HasFlag(DelegateAccess.SendOnBehalf);
    }

    /// <summary>The permission set the user selected.</summary>
    public DelegateAccess SelectedAccess =>
        (FullAccess ? DelegateAccess.FullAccess : 0)
        | (SendAs ? DelegateAccess.SendAs : 0)
        | (SendOnBehalf ? DelegateAccess.SendOnBehalf : 0);
}
