using System.Globalization;
using System.Windows;
using System.Windows.Data;
using UnifiedDirectoryManager.Models;

namespace UnifiedDirectoryManager.Views;

/// <summary>Binds a radio button / toggle to one enum value (ConverterParameter = enum member name).</summary>
public sealed class EnumMatchToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string name &&
        string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name, true) : Binding.DoNothing;
}

/// <summary>Inverts a boolean (e.g. IsBusy → IsEnabled).</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}

/// <summary>True when the bound value is non-null / non-empty (for enabling buttons).</summary>
public sealed class HasValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => false,
        string s => !string.IsNullOrWhiteSpace(s),
        int i => i > 0,
        _ => true,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Visible when the bound string is non-empty, otherwise Collapsed.</summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Maps a ScenarioActionType to a human-readable label for the step editor.</summary>
public sealed class ScenarioActionToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ScenarioActionType a ? a switch
        {
            ScenarioActionType.Disable => "Disable account",
            ScenarioActionType.Enable => "Enable account",
            ScenarioActionType.Unlock => "Unlock account",
            ScenarioActionType.RemoveAllGroups => "Remove all group memberships",
            ScenarioActionType.AddToGroups => "Add to groups",
            ScenarioActionType.RemoveFromGroups => "Remove from groups",
            ScenarioActionType.SetAttribute => "Set attribute",
            ScenarioActionType.ClearAttribute => "Clear attribute",
            ScenarioActionType.SetDescription => "Set description",
            ScenarioActionType.MoveToOu => "Move to OU",
            ScenarioActionType.CloudDisableAccount => "Cloud: disable account",
            ScenarioActionType.CloudEnableAccount => "Cloud: enable account",
            ScenarioActionType.CloudRevokeSessions => "Cloud: revoke sign-in sessions",
            ScenarioActionType.CloudAddToGroups => "Cloud: add to groups",
            ScenarioActionType.CloudRemoveFromGroups => "Cloud: remove from groups",
            ScenarioActionType.CloudRemoveAllGroups => "Cloud: remove from all groups",
            ScenarioActionType.ExchangeConvertToShared => "Exchange: convert to shared mailbox",
            ScenarioActionType.ExchangeConvertToRegular => "Exchange: convert to regular mailbox",
            ScenarioActionType.ExchangeSetForwarding => "Exchange: set mailbox forwarding",
            ScenarioActionType.ExchangeClearForwarding => "Exchange: clear mailbox forwarding",
            ScenarioActionType.ExchangeDelegateToManager => "Exchange: delegate mailbox to manager",
            ScenarioActionType.SaveOperationLog => "Save operation log",
            _ => value.ToString() ?? string.Empty,
        } : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Maps an AdObjectType to a simple glyph for the tree / list.</summary>
public sealed class ObjectTypeToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AdObjectType t ? t switch
        {
            AdObjectType.Domain => "🌐",
            AdObjectType.OrganizationalUnit => "📁",
            AdObjectType.Container => "🗂",
            AdObjectType.User => "👤",
            AdObjectType.Computer => "💻",
            AdObjectType.Group => "👥",
            AdObjectType.Contact => "📇",
            _ => "•",
        } : "•";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
