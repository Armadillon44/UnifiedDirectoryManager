using System.ComponentModel;
using System.Reflection;
using System.Windows;

namespace UnifiedDirectoryManager.Services;

/// <summary>
/// Makes menus and submenus open to the RIGHT, whatever the machine's menu-drop-alignment setting says.
/// </summary>
/// <remarks>
/// <para>
/// Windows exposes a per-user setting, <c>SM_MENUDROPALIGNMENT</c>, that right-aligns menus: a dropdown
/// aligns its right edge with its header instead of its left, and every submenu flies out to the LEFT of
/// its parent. It exists for left-handed pen use, and it is switched on by the Tablet PC handedness
/// setting — so it turns up on Surfaces, on touchscreen laptops, and on any machine with a pen
/// digitizer, often without the operator ever choosing it. WPF reads it once per process and obeys it
/// everywhere.
/// </para>
/// <para>
/// This app is a directory tool with a deep Action ▸ New submenu, and a menu bar an operator is meant to
/// be able to be *told* about ("it's under Action ▸ New"). Menus opening in mirror image on some machines
/// and not others defeats that, and it cannot be fixed in XAML: there is no public API, because the
/// setting is deliberately global.
/// </para>
/// <para>
/// So the cached value is overwritten by reflection. That is not something to do lightly, and the field
/// may be renamed or removed in a future .NET — hence every step is optional and failure is silent. The
/// worst case is exactly the behaviour we have without this.
/// </para>
/// <para>
/// Naming, because the two halves read backwards: <c>SystemParameters.MenuDropAlignment</c> is true when
/// menus are RIGHT-aligned, which is what makes them open LEFTWARDS. Everything here is named for the
/// direction menus open, which is the thing anyone looking at this is actually trying to change.
/// </para>
/// </remarks>
internal static class MenuDropAlignment
{
    private const string CachedField = "_menuDropAlignment";
    private static bool _watching;

    /// <summary>Whether menus currently open rightwards (the normal, right-handed behaviour).</summary>
    internal static bool MenusOpenRightwards => !SystemParameters.MenuDropAlignment;

    /// <summary>
    /// Forces menus to open rightwards, and keeps them that way if Windows broadcasts a settings change
    /// later in the session. Safe to call more than once.
    /// </summary>
    internal static void ForceMenusToOpenRightwards()
    {
        Apply();

        // WM_SETTINGCHANGE makes WPF re-read the setting from the OS, which would undo this mid-session
        // — plugging in a pen tablet is enough. Writing the field directly raises nothing, so re-applying
        // from the event cannot recurse. Subscribe once, however often this is called.
        if (_watching) return;
        _watching = true;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    private static void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(SystemParameters.MenuDropAlignment)) Apply();
    }

    private static void Apply()
    {
        try
        {
            // The read is load-bearing, and the ORDER is the whole trick. SystemParameters caches this
            // lazily: the getter asks Windows on FIRST access and stores the answer. Writing the field
            // before anything has read it is silently undone by that first read — the override appears to
            // work, and does nothing. Touching the property here populates the cache, so the write below
            // is the one that survives. test-ui-placement proves this by doing it in the wrong order.

            if (MenusOpenRightwards) return;

            typeof(SystemParameters)
                .GetField(CachedField, BindingFlags.NonPublic | BindingFlags.Static)
                ?.SetValue(null, false);
        }
        catch (Exception ex)
        {
            // Never worth failing startup over. Menus open the wrong way; everything still works.
            AppLog.Instance.Warn($"Could not override the menu drop alignment: {ex.Message}");
        }
    }

    /// <summary>
    /// Pretends the machine is configured to open menus leftwards, so the override can be tested on a
    /// machine that is not. Returns false if the framework field this depends on has gone.
    /// </summary>
    internal static bool SimulateLeftwardMenus()
    {
        _ = SystemParameters.MenuDropAlignment; // populate the cache, for the reason given in Apply
        var field = typeof(SystemParameters).GetField(CachedField, BindingFlags.NonPublic | BindingFlags.Static);
        if (field is null) return false;
        field.SetValue(null, true);
        return SystemParameters.MenuDropAlignment;
    }
}
