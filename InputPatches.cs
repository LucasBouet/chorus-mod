using System;
using HarmonyLib;
using UnityEngine;

namespace ChorusMod;

/// <summary>
/// Stops Clone Hero from reacting to keys while typing.
///
/// What was tried before, and why it wasn't enough:
///   1. GameObject.SetActive(false) on "Rewired Input Manager"
///      -> deinitializes Rewired permanently. Avoid.
///   2. Behaviour.enabled = false on its components
///      -> same consequence.
///   3. ReInput.controllers.Keyboard.enabled = false
///      -> applies correctly (verified by reading the flag back), but the
///         Control Remapper would still open: the game reads that key
///         through Unity's legacy Input, not through Rewired.
///
/// Hence this approach: a Harmony prefix on UnityEngine.Input, which
/// returns "key not pressed" while the overlay is open. No state is
/// modified, and the patch is removed cleanly when the game closes.
/// </summary>
public static class InputPatches
{
    /// Enabled by ChorusUI while the panel is open.
    public static bool SwallowKeys;

    private static Harmony? _harmony;

    public static void Apply(string id)
    {
        try
        {
            _harmony = new Harmony(id);

            Patch(nameof(Input.GetKeyDown), nameof(GetKeyPrefix));
            Patch(nameof(Input.GetKey), nameof(GetKeyPrefix));
            Patch(nameof(Input.GetKeyUp), nameof(GetKeyPrefix));

            Plugin.Logger.LogInfo("Keyboard input patch applied.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning(
                $"Input patch failed, keystrokes will also reach the game: {e}"
            );
        }
    }

    private static void Patch(string unityMethod, string prefixName)
    {
        // Only the KeyCode overload: the string-based one goes through
        // configured axes, not physical keys.
        var target = AccessTools.Method(
            typeof(Input),
            unityMethod,
            new[] { typeof(KeyCode) }
        );

        if (target == null)
        {
            Plugin.Logger.LogWarning($"Input.{unityMethod}(KeyCode) not found.");
            return;
        }

        var prefix = AccessTools.Method(typeof(InputPatches), prefixName);
        _harmony!.Patch(target, prefix: new HarmonyMethod(prefix));
    }

    /// Returns false (= key not pressed) to the game while the overlay is
    /// open. The toggle key stays readable, otherwise we couldn't close the
    /// panel from our own Update() anymore.
    private static bool GetKeyPrefix(KeyCode key, ref bool __result)
    {
        if (!SwallowKeys || key == Plugin.ToggleKey.Value)
        {
            return true; // let the original call through
        }

        __result = false;
        return false; // short-circuit the original call
    }

    public static void Dispose()
    {
        try
        {
            _harmony?.UnpatchSelf();
        }
        catch (Exception)
        {
            // Nothing more to do on shutdown.
        }
    }
}
