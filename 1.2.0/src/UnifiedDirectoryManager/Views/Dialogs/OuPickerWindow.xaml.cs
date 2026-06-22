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

    public OuPickerWindow(IDirectoryService directory, IEnumerable<string>? initialDns, bool multiSelect)
    {
        InitializeComponent();
        MultiSelect = multiSelect;

        var root = new TreeNodeViewModel(directory.GetRootNode(), directory, _ => { },
            multiSelect ? UpdateCheckedSummary : null);
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
            SelectedDn = initialDns?.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
            SelectedText.Text = string.IsNullOrEmpty(SelectedDn) ? "(nothing selected)" : SelectedDn;
        }
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
        var count = CheckedNodes().Count();
        SelectedText.Text = count == 0
            ? "(no OUs ticked — leaving these empty searches the whole domain)"
            : $"{count} OU(s) ticked";
    }

    /// <summary>Walks the loaded tree returning every ticked, non-placeholder node.</summary>
    private IEnumerable<TreeNodeViewModel> CheckedNodes()
    {
        var stack = new Stack<TreeNodeViewModel>(RootNodes);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.IsChecked && !node.IsPlaceholder) yield return node;
            foreach (var child in node.Children) stack.Push(child);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (MultiSelect)
        {
            SelectedDns = CheckedNodes()
                .Select(n => n.DistinguishedName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else if (string.IsNullOrEmpty(SelectedDn))
        {
            return; // require a selection before OK closes
        }

        DialogResult = true;
        Close();
    }
}
