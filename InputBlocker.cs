using System;
using Rewired;

namespace ChorusMod;

/// <summary>
/// Stops Clone Hero from reacting to keystrokes while typing.
///
/// WHAT NOT TO DO (tested, broken):
///   - GameObject.SetActive(false) on "Rewired Input Manager"
///   - Behaviour.enabled = false on its components
/// Both trigger Rewired's internal deinitialization ("Rewired is not
/// initialized" looping) and aren't reversible without restarting the game.
///
/// WHAT'S ACTUALLY MEANT FOR THIS: disable just the keyboard controller via
/// ReInput.controllers.Keyboard.enabled. It's a normal API switch,
/// reversible, and controllers/guitars stay active.
/// </summary>
public static class InputBlocker
{
    private static bool _unavailable;

    public static void SetKeyboardEnabled(bool enabled)
    {
        if (_unavailable || !Plugin.BlockGameInput.Value)
        {
            return;
        }

        try
        {
            if (!ReInput.isReady)
            {
                Plugin.Logger.LogWarning(
                    "ReInput.isReady == false: can't disable the keyboard."
                );
                return;
            }

            var controllers = ReInput.controllers;
            if (controllers == null)
            {
                Plugin.Logger.LogWarning("ReInput.controllers == null.");
                return;
            }

            var keyboard = controllers.Keyboard;
            if (keyboard == null)
            {
                Plugin.Logger.LogWarning("ReInput.controllers.Keyboard == null.");
                return;
            }

            keyboard.enabled = enabled;

            // Read the value back: if it didn't stick, this version of
            // Rewired ignores the flag and this approach is a dead end.
            Plugin.Logger.LogInfo(
                $"Rewired keyboard: requested enabled={enabled}, read back={keyboard.enabled}"
            );
        }
        catch (Exception e)
        {
            // Method not restored by Cpp2IL, or a different API on this
            // version of Rewired: bail out cleanly, just once.
            _unavailable = true;
            Plugin.Logger.LogWarning(
                "Keyboard blocking unavailable, keystrokes will also reach "
                    + $"the game while typing: {e.Message}"
            );
        }
    }
}
