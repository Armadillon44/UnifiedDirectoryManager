using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using UnifiedDirectoryManager.Services;
using UnifiedDirectoryManager.ViewModels;

namespace UnifiedDirectoryManager.Views.Dialogs;

public partial class OuPickerWindow : Window
{
    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = new();

    /// <summary>True when the picker lets the user tick several OUs instead of choosing a single node.</summary>
    public bool MultiSelect { get; }

    /// <summary>The DN of the chosen container in single-select mode (valid after the dialog returns true).</summary>
    public string? SelectedDn { get; private set; }

    /// <summary>The DNs of every ticked container in multi-select mode (valid after the dialog returns true).</summary>
    public IReadOnlyList<string> SelectedDns { get; private set; } = Array.Empty<string>();

    /// <summary>Single-select picker (used to choose one parent container/OU).</summary>
    public OuPickerWindow(IDirectoryService directory, string? initialDn)
        : this(directory, initialDn is null ? null : new[] { initialDn }, multiSelect: false)
    {
    }

    /// <summary>
    /// What the picker will return in multi-select mode. Seeded from the caller's existing selection and
    /// edited by ticking, rather than derived from the tree when OK is pressed.
    ///
    /// Deriving it from the tree is what lost the seed twice over. The tree loads LAZILY, so an OU the
    /// caller passed in may not exist as a node yet — Advanced Search's "pick OUs" hands over the search
    /// bases it already has, and those can be anywhere in the domain. Walking only what happened to be
    /// loaded meant OK returned an empty list and the operator's scoping silently evaporated.
    /// </summary>
    private readonly HashSet<string> _selected;

    public OuPickerWindow(IDirectoryService directory, IEnumerable<string>? initialDns, bool multiSelect)
    {
        InitializeComponent();
        MultiSelect = multiSelect;

        _selected = new HashSet<string>(
            (initialDns ?? Array.Empty<string>())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim()),
            StringComparer.OrdinalIgnoreCase);

        // The seed goes into the tree itself, so each node — including one loaded later, when its parent is
        // expanded — arrives already ticked. Doing it here, once, is the only place that can be true of
        // nodes that do not exist yet.
        var root = new TreeNodeViewModel(directory.GetRootNode(), directory, _ => { },
            multiSelect ? OnCheckChanged : null,
            checkedDns: multiSelect ? _selected : null);
        RootNodes.Add(root);
        root.IsExpanded = true;
        Tree.ItemsSource = RootNodes;

        if (multiSelect)
        {
            Title = "Select OUs to search";
            UpdateCheckedSummary();
        }
        else
        {
            SelectedDn = _selected.FirstOrDefault();
            SelectedText.Text = string.IsNullOrEmpty(SelectedDn) ? "(nothing selected)" : SelectedDn;
        }
    }

    /// <summary>
    /// Folds a tick or untick into <see cref="_selected"/>.
    ///
    /// Only nodes that are actually LOADED can speak: an unticked node means "the operator cleared this",
    /// but a node that has not loaded means nothing at all, and treating its absence as a clear is precisely
    /// how the seeded selection used to disappear.
    /// </summary>
    private void OnCheckChanged()
    {
        foreach (var node in LoadedNodes())
        {
            if (node.IsChecked) _selected.Add(node.DistinguishedName);
            else _selected.Remove(node.DistinguishedName);
        }
        UpdateCheckedSummary();
    }

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (MultiSelect) return; // ticking, not highlighting, drives the multi-select picker
        if (e.NewValue is TreeNodeViewModel { IsPlaceholder: false } node)
        {
            SelectedDn = node.DistinguishedName;
            SelectedText.Text = node.DistinguishedName;
        }
    }

    private void UpdateCheckedSummary()
    {
        // Counts the SELECTION, not the visible ticks. A seeded OU deep in the tree is selected even though
        // its node has not loaded, and saying "0 ticked" over a selection of four would be a lie the
        // operator acts on.
        SelectedText.Text = _selected.Count == 0
            ? "(no OUs ticked — leaving these empty searches the whole domain)"
            : $"{_selected.Count} OU(s) ticked";
    }

    /// <summary>Walks the loaded tree returning every real (non-placeholder) node with a distinguished name.</summary>
    private IEnumerable<TreeNodeViewModel> LoadedNodes()
    {
        var stack = new Stack<TreeNodeViewModel>(RootNodes);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!node.IsPlaceholder && !string.IsNullOrEmpty(node.DistinguishedName)) yield return node;
            foreach (var child in node.Children) stack.Push(child);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (MultiSelect)
        {
            // The selection, not a walk of whatever happened to be loaded.
            SelectedDns = _selected.ToList();
        }
        else if (string.IsNullOrEmpty(SelectedDn))
        {
            return; // require a selection before OK closes
        }

        DialogResult = true;
        Close();
    }
}
