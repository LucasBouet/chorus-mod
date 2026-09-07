using System;
using HarmonyLib;
using UnityEngine;

namespace ChorusMod;

/// <summary>
/// Empêche Clone Hero de réagir aux touches pendant la saisie.
///
/// Ce qui a été essayé avant, et pourquoi ça ne suffisait pas :
///   1. GameObject.SetActive(false) sur "Rewired Input Manager"
///      -> désinitialise Rewired définitivement. À proscrire.
///   2. Behaviour.enabled = false sur ses composants
///      -> même conséquence.
///   3. ReInput.controllers.Keyboard.enabled = false
///      -> s'applique correctement (vérifié par relecture du flag), mais
///         le Control Remapper s'ouvrait quand même : le jeu lit cette
///         touche via l'Input legacy d'Unity, pas via Rewired.
///
/// D'où cette approche : préfixe Harmony sur UnityEngine.Input, qui
/// renvoie "touche non pressée" tant que l'overlay est ouvert. Aucun état
/// n'est modifié, le patch est retiré proprement à la fermeture du jeu.
/// </summary>
public static class InputPatches
{
    /// Activé par ChorusUI pendant que le panneau est ouvert.
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

            Plugin.Logger.LogInfo("Patch d'entrée clavier appliqué.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning(
                $"Patch d'entrée impossible, les touches iront aussi au jeu : {e}"
            );
        }
    }

    private static void Patch(string unityMethod, string prefixName)
    {
        // Uniquement la surcharge KeyCode : celle qui prend une string
        // passe par les axes configurés, pas par les touches physiques.
        var target = AccessTools.Method(
            typeof(Input),
            unityMethod,
            new[] { typeof(KeyCode) }
        );

        if (target == null)
        {
            Plugin.Logger.LogWarning($"Input.{unityMethod}(KeyCode) introuvable.");
            return;
        }

        var prefix = AccessTools.Method(typeof(InputPatches), prefixName);
        _harmony!.Patch(target, prefix: new HarmonyMethod(prefix));
    }

    /// Renvoie false (= touche non pressée) au jeu tant que l'overlay est
    /// ouvert. La touche d'ouverture reste lisible, sinon on ne pourrait
    /// plus refermer le panneau depuis notre propre Update().
    private static bool GetKeyPrefix(KeyCode key, ref bool __result)
    {
        if (!SwallowKeys || key == Plugin.ToggleKey.Value)
        {
            return true; // laisse passer l'appel original
        }

        __result = false;
        return false; // court-circuite l'appel original
    }

    public static void Dispose()
    {
        try
        {
            _harmony?.UnpatchSelf();
        }
        catch (Exception)
        {
            // Rien à faire de plus à l'extinction.
        }
    }
}
