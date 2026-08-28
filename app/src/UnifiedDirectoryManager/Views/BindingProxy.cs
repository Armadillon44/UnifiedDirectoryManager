using System.Windows;

namespace UnifiedDirectoryManager.Views;

/// <summary>
/// Carries a DataContext into places the visual tree doesn't reach. <c>CollectionContainer</c> inside a
/// <c>CompositeCollection</c> is the case this exists for: it is not part of the visual tree, so a plain
/// <c>{Binding Sections}</c> on it silently resolves to nothing.
///
/// A <see cref="Freezable"/> is used because Freezables inherit the DataContext of the element whose resources
/// hold them, which a plain object does not.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
