using System;
using Rewired;

namespace ChorusMod;

/// <summary>
/// Empêche Clone Hero de réagir aux frappes pendant la saisie.
///
/// CE QU'IL NE FAUT PAS FAIRE (testé, cassé) :
///   - GameObject.SetActive(false) sur "Rewired Input Manager"
///   - Behaviour.enabled = false sur ses composants
/// Les deux déclenchent la désinitialisation interne de Rewired
/// ("Rewired is not initialized" en boucle) et ne sont pas réversibles
/// sans redémarrer le jeu.
///
/// CE QUI EST PRÉVU POUR ÇA : couper le seul contrôleur clavier via
/// ReInput.controllers.Keyboard.enabled. C'est un interrupteur normal de
/// l'API, réversible, et les manettes/guitares restent actives.
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
                    "ReInput.isReady == false : impossible de couper le clavier."
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

            // On relit la valeur : si elle n'a pas pris, le flag est ignoré
            // par cette version de Rewired et la piste est morte.
            Plugin.Logger.LogInfo(
                $"Clavier Rewired : demandé enabled={enabled}, relu={keyboard.enabled}"
            );
        }
        catch (Exception e)
        {
            // Méthode non restaurée par Cpp2IL, ou API différente sur cette
            // version de Rewired : on abandonne proprement, une seule fois.
            _unavailable = true;
            Plugin.Logger.LogWarning(
                "Blocage du clavier indisponible, les touches atteindront "
                    + $"aussi le jeu pendant la saisie : {e.Message}"
            );
        }
    }
}
