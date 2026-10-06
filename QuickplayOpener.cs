using System;
using System.Reflection;
using UnityEngine;

namespace ChorusMod;

/// <summary>
/// Opens Quickplay from the main menu the way a player does: highlight
/// the "Quickplay" entry, then confirm it.
///
/// Found by watching the game (the MenuProbe diagnostic): the highlighted
/// entry is BaseMenu's field_Protected_Int32_1, and confirming it calls
/// MainMenu.Method_Public_Virtual_Void_1 (checked on 1.1.0.6142). Those
/// names are generated and may differ on other game builds: it's tried
/// anyway, and if anything is missing or fails the player just opens
/// Quickplay themselves (the chart still gets selected).
/// </summary>
public static class QuickplayOpener
{
    private const string ConfirmMethod = "Method_Public_Virtual_Void_1";

    private static MethodInfo? _confirm;
    private static bool _searched;

    /// Whether the confirm method exists on this game build.
    public static bool Supported => Confirm() != null;

    /// True if Quickplay was asked to open.
    public static bool TryOpen()
    {
        if (!Supported)
        {
            Plugin.Logger.LogInfo($"Quickplay opener: no {ConfirmMethod} on game build {Application.version}, open it by hand.");
            return false;
        }

        try
        {
            var menu = UnityEngine.Object.FindObjectOfType<MainMenu>();
            if (menu == null || !menu.isActive || BaseMenu.transitioning)
            {
                Plugin.Logger.LogInfo("Quickplay opener: the main menu isn't ready.");
                return false;
            }

            // The entry is looked up by its text, never assumed to be 0.
            var index = -1;
            var entries = menu.menuStrings;
            for (var i = 0; entries != null && i < entries.Length; i++)
            {
                if (string.Equals(entries[i], "Quickplay", StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                Plugin.Logger.LogInfo("Quickplay opener: no Quickplay entry on the main menu.");
                return false;
            }

            menu.field_Protected_Int32_1 = index;
            if (menu.field_Protected_Int32_1 != index)
            {
                Plugin.Logger.LogInfo("Quickplay opener: couldn't highlight Quickplay.");
                return false;
            }

            Confirm()!.Invoke(menu, null);
            Plugin.Logger.LogInfo("Quickplay opener: Quickplay confirmed.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Quickplay opener failed: {(e as TargetInvocationException)?.InnerException?.Message ?? e.Message}");
            return false;
        }
    }

    private static MethodInfo? Confirm()
    {
        if (!_searched)
        {
            _searched = true;
            _confirm = typeof(MainMenu).GetMethod(
                ConfirmMethod,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                null,
                Type.EmptyTypes,
                null
            );
        }

        return _confirm;
    }
}
