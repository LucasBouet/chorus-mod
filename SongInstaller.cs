using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChorusMod;

public static class SongInstaller
{
    /// Écrit les octets téléchargés dans le dossier Songs. Détecte
    /// automatiquement zip vs fichier .sng isolé.
    /// À appeler depuis un thread de fond (I/O bloquante).
    public static string Install(byte[] data, string songName)
    {
        var root = Plugin.SongsFolder.Value;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                "Dossier Songs non configuré (voir BepInEx/config/fr.lucas.chorus-mod.cfg)."
            );
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Dossier Songs introuvable : {root}");
        }

        var folderName = Sanitize(songName);
        var targetDir = Path.Combine(root, folderName);

        if (IsZip(data))
        {
            ExtractZip(data, targetDir);
        }
        else
        {
            // Pas un zip : très probablement un .sng seul. On le pose dans
            // son propre dossier pour que le scanner le voie proprement.
            Directory.CreateDirectory(targetDir);
            File.WriteAllBytes(Path.Combine(targetDir, folderName + ".sng"), data);
        }

        return targetDir;
    }

    private static bool IsZip(byte[] data) =>
        data.Length >= 2 && data[0] == 'P' && data[1] == 'K';

    private static void ExtractZip(byte[] data, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        using var stream = new MemoryStream(data);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var fullTarget = Path.GetFullPath(targetDir);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // dossier
            }

            var destPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));

            // Garde-fou zip-slip : on refuse toute entrée qui sortirait du
            // dossier cible via des ../ dans son chemin.
            if (!destPath.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Logger.LogWarning($"Entrée zip suspecte ignorée : {entry.FullName}");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    /// Caractères interdits sous Windows. On applique la liste la plus
    /// stricte sur TOUTES les plateformes, volontairement :
    /// Path.GetInvalidFileNameChars() ne renvoie que '/' et '\0' sous
    /// Linux, ce qui produirait des dossiers du type "AC/DC - T.N.T" —
    /// illisibles si la bibliothèque est ensuite partagée avec une machine
    /// Windows ou synchronisée.
    private static readonly char[] ForbiddenChars =
    {
        '<', '>', ':', '"', '/', '\\', '|', '?', '*',
    };

    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            // Caractères de contrôle inclus (0-31), interdits partout.
            builder.Append(
                c < 32 || Array.IndexOf(ForbiddenChars, c) >= 0 ? '_' : c
            );
        }

        // Windows refuse aussi les points/espaces en fin de nom.
        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');

        return cleaned.Length == 0 ? "chart" : cleaned;
    }

    /// Déclenche le rescan de la bibliothèque.
    /// DOIT être appelé sur le thread principal Unity.
    public static bool TriggerRescan()
    {
        var songScan = FindSongScan();
        if (songScan == null)
        {
            Plugin.Logger.LogWarning(
                "SongScan introuvable (ni actif ni inactif) : lance le scan "
                    + "manuellement depuis le menu du jeu."
            );
            return false;
        }

        if (songScan.isScanning)
        {
            Plugin.Logger.LogInfo("Un scan est déjà en cours.");
            return false;
        }

        // false = scan incrémental. Si les nouveaux morceaux n'apparaissent
        // pas, passe FullScan à true dans le .cfg.
        var full = Plugin.FullScan.Value;
        Plugin.Logger.LogInfo($"Lancement du rescan (fullScan={full})…");
        songScan.Method_Public_Coroutine_Boolean_0(full);
        return true;
    }

    /// Object.FindObjectOfType ignore les GameObjects désactivés, or
    /// SongScan vit sur l'overlay de scan qui est caché la plupart du
    /// temps. Resources.FindObjectsOfTypeAll, lui, les voit aussi.
    private static SongScan? FindSongScan()
    {
        try
        {
            var all = Resources.FindObjectsOfTypeAll<SongScan>();
            if (all != null && all.Length > 0)
            {
                Plugin.Logger.LogInfo($"SongScan trouvé ({all.Length} instance(s)).");
                return all[0];
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"FindObjectsOfTypeAll indisponible : {e.Message}");
        }

        // Repli sur la recherche classique (objets actifs uniquement).
        return Object.FindObjectOfType<SongScan>();
    }
}
