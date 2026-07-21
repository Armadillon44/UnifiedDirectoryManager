using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>A cloud (Entra ID) tree node's role (null on on-prem AD nodes).</summary>
public enum CloudNodeKind { Tenant, Users, Groups, Devices }

/// <summary>A node in the directory tree. On-prem children load lazily on first expand; cloud nodes are
/// a fixed structure (Entra ID ▸ Users/Groups/Devices) that loads objects into the content pane instead.</summary>
public partial class TreeNodeViewModel : ObservableObject
{
    private readonly IDirectoryService _directory;
    private readonly Action<string> _onError;
    private readonly Action? _onCheckChanged;
    private bool _loaded;

    public AdNode Node { get; }
    public bool IsPlaceholder { get; }

    /// <summary>Non-null for the Entra ID section nodes; null for on-prem AD nodes.</summary>
    public CloudNodeKind? CloudKind { get; }

    public string Name => Node.Name;
    public AdObjectType Type => Node.Type;
    public string DistinguishedName => Node.DistinguishedName;

    /// <summary>True for an on-prem OU / container / domain node (the tree's folder nodes) — the targets for the
    /// right-click Properties action. Cloud (Entra) section nodes and the "Loading…" placeholder are excluded.</summary>
    public bool IsContainerNode => CloudKind is null && !IsPlaceholder
        && Type is AdObjectType.OrganizationalUnit or AdObjectType.Container or AdObjectType.Domain;

    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;

    /// <summary>Whether this node is ticked in the multi-select OU picker (ignored elsewhere).</summary>
    [ObservableProperty] private bool _isChecked;

    public TreeNodeViewModel(AdNode node, IDirectoryService directory, Action<string> onError,
        Action? onCheckChanged = null, bool isPlaceholder = false)
    {
        Node = node;
        _directory = directory;
        _onError = onError;
        _onCheckChanged = onCheckChanged;
        IsPlaceholder = isPlaceholder;

        if (!isPlaceholder && node.HasChildren)
            Children.Add(new TreeNodeViewModel(
                new AdNode { DistinguishedName = string.Empty, Name = "Loading…", HasChildren = false },
                directory, onError, isPlaceholder: true));
    }

    /// <summary>Creates a cloud (Entra ID) node with a fixed set of children (no lazy AD load).</summary>
    public TreeNodeViewModel(CloudNodeKind kind, string name, IDirectoryService directory, Action<string> onError,
        IEnumerable<TreeNodeViewModel>? children = null)
    {
        Node = new AdNode { DistinguishedName = $"cloud:{kind}", Name = name, Type = AdObjectType.Unknown, HasChildren = false };
        _directory = directory;
        _onError = onError;
        CloudKind = kind;
        if (children is not null)
            foreach (var child in children) Children.Add(child);
    }

    /// <summary>Glyph for the tree row (cloud kind takes precedence over the AD object type).</summary>
    public string Glyph => CloudKind switch
    {
        CloudNodeKind.Tenant => "☁",
        CloudNodeKind.Users => "👤",
        CloudNodeKind.Groups => "👥",
        CloudNodeKind.Devices => "💻",
        _ => Type switch
        {
            AdObjectType.Domain => "🌐",
            AdObjectType.OrganizationalUnit => "📁",
            AdObjectType.Container => "🗂",
            AdObjectType.User => "👤",
            AdObjectType.Computer => "💻",
            AdObjectType.Group => "👥",
            AdObjectType.Contact => "📇",
            _ => "•",
        },
    };

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && CloudKind is null) _ = EnsureChildrenAsync();
    }

    partial void OnIsCheckedChanged(bool value) => _onCheckChanged?.Invoke();

    /// <summary>Loads child containers once (on-prem only). Safe to call repeatedly.</summary>
    public async Task EnsureChildrenAsync()
    {
        if (_loaded || CloudKind is not null) return;
        _loaded = true;
        Children.Clear();
        try
        {
            var children = await _directory.GetChildrenAsync(DistinguishedName);
            foreach (var child in children)
                Children.Add(new TreeNodeViewModel(child, _directory, _onError, _onCheckChanged));
        }
        catch (Exception ex)
        {
            _loaded = false;
            _onError(DirectoryService.Friendly(ex));
        }
    }

    /// <summary>Forces a reload of children on next access (e.g. after creating an object).</summary>
    public void Invalidate() => _loaded = false;
}
