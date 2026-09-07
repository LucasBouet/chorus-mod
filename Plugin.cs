using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace ChorusMod;

[BepInPlugin("fr.lucas.chorus-mod", "Chorus Mod", "1.0.0")]
public class Plugin : BasePlugin
{
    public static ManualLogSource Logger = null!;

    public static ConfigEntry<string> SongsFolder = null!;
    public static ConfigEntry<string> ApiBaseUrl = null!;
    public static ConfigEntry<string> SearchEndpoint = null!;
    public static ConfigEntry<string> FilesBaseUrl = null!;
    public static ConfigEntry<string> Instrument = null!;
    public static ConfigEntry<bool> LogRawResponse = null!;
    public static ConfigEntry<KeyCode> ToggleKey = null!;
    public static ConfigEntry<bool> BlockGameInput = null!;
    public static ConfigEntry<bool> FullScan = null!;
    public static ConfigEntry<int> PanelOpacityLayers = null!;
    public static ConfigEntry<float> PanelWidth = null!;
    public static ConfigEntry<float> PanelHeight = null!;
    public static ConfigEntry<bool> ShowAlbumArt = null!;

    public override void Load()
    {
        Logger = Log;

        SongsFolder = Config.Bind(
            "General",
            "SongsFolder",
            DetectSongsFolder(),
            "Dossier où les charts téléchargées sont installées. "
                + "Doit être un des dossiers que Clone Hero scanne."
        );

        ToggleKey = Config.Bind(
            "General",
            "ToggleKey",
            KeyCode.F9,
            "Touche pour ouvrir/fermer la fenêtre de recherche."
        );

        BlockGameInput = Config.Bind(
            "General",
            "BlockGameInput",
            true,
            "Coupe le contrôleur clavier de Rewired pendant que le panneau "
                + "est ouvert, pour que les touches tapées ne déclenchent pas "
                + "les raccourcis de Clone Hero (Espace, etc.). Les manettes "
                + "et guitares restent actives. Mets à false en cas de souci."
        );

        FullScan = Config.Bind(
            "General",
            "FullScan",
            false,
            "false = scan incrémental après installation (rapide). Passe à "
                + "true si les nouveaux morceaux n'apparaissent pas."
        );

        PanelWidth = Config.Bind(
            "General",
            "PanelWidth",
            1180f,
            "Largeur de l'overlay en pixels. Monte-la sur un grand écran."
        );

        PanelHeight = Config.Bind(
            "General",
            "PanelHeight",
            780f,
            "Hauteur de l'overlay en pixels."
        );

        ShowAlbumArt = Config.Bind(
            "General",
            "ShowAlbumArt",
            true,
            "Affiche les jaquettes dans la liste. Passe à false si le "
                + "chargement des images pose problème ou ralentit."
        );

        PanelOpacityLayers = Config.Bind(
            "General",
            "PanelOpacityLayers",
            8,
            "Opacité du fond du panneau quand la teinte GUI.color n'est pas "
                + "disponible : nombre de passes de dessin empilées. "
                + "Augmente si le fond reste trop transparent."
        );

        // Endpoint confirmé par capture réseau du site (POST + body JSON).
        ApiBaseUrl = Config.Bind(
            "API",
            "BaseUrl",
            "https://api.enchor.us",
            "Base URL de l'API Chorus Encore."
        );

        SearchEndpoint = Config.Bind(
            "API",
            "SearchEndpoint",
            "/search",
            "Chemin de l'endpoint de recherche (appelé en POST)."
        );

        FilesBaseUrl = Config.Bind(
            "API",
            "FilesBaseUrl",
            "https://files.enchor.us",
            "Hôte des fichiers, utilisé pour reconstruire un lien de "
                + "téléchargement quand la réponse ne fournit qu'un hash."
        );

        Instrument = Config.Bind(
            "API",
            "Instrument",
            "guitar",
            "Filtre instrument envoyé au serveur (guitar, bass, drums, "
                + "keys, vocals…). Mets 'null' pour ne pas filtrer."
        );

        LogRawResponse = Config.Bind(
            "API",
            "LogRawResponse",
            true,
            "Écrit le JSON brut du premier résultat dans le log. Utile pour "
                + "ajuster le parsing ; à repasser à false une fois que tout "
                + "fonctionne."
        );

        Logger.LogInfo($"Chorus Mod chargé. Dossier Songs : '{SongsFolder.Value}'");
        if (string.IsNullOrWhiteSpace(SongsFolder.Value))
        {
            Logger.LogWarning(
                "Aucun dossier Songs détecté automatiquement. Renseigne "
                    + "SongsFolder dans BepInEx/config/fr.lucas.chorus-mod.cfg, "
                    + "sinon les téléchargements échoueront."
            );
        }

        if (BlockGameInput.Value)
        {
            InputPatches.Apply("fr.lucas.chorus-mod");
        }

        // Un MonoBehaviour custom doit être enregistré auprès du runtime
        // IL2CPP avant de pouvoir être attaché à un GameObject.
        ClassInjector.RegisterTypeInIl2Cpp<ChorusUI>();

        var host = new GameObject("ChorusMod.UI");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<ChorusUI>();

        Logger.LogInfo($"Appuie sur {ToggleKey.Value} en jeu pour ouvrir Chorus Mod.");
    }

    /// Best-effort, multiplateforme : cherche un dossier Songs plausible.
    ///
    /// Clone Hero laisse l'utilisateur définir ses propres chemins de
    /// bibliothèque, donc aucune détection ne peut être fiable à 100 % --
    /// d'où le réglage SongsFolder en secours. On ne code en dur aucun
    /// chemin absolu : tout est reconstruit depuis les dossiers spéciaux
    /// de l'OS et depuis l'emplacement réel du jeu.
    private static string DetectSongsFolder()
    {
        foreach (var path in CandidateSongFolders())
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                return path;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> CandidateSongFolders()
    {
        // 1. À côté de l'exécutable : valable sur toutes les plateformes.
        yield return Path.Combine(Paths.GameRootPath, "Songs");

        var documents = Environment.GetFolderPath(
            Environment.SpecialFolder.MyDocuments
        );
        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile
        );

        // 2. Emplacement « à la Windows », qui existe aussi sous Proton et
        //    que .NET mappe vers ~/Documents sous Linux.
        if (!string.IsNullOrEmpty(documents))
        {
            yield return Path.Combine(documents, "Clone Hero", "Songs");
            yield return Path.Combine(documents, "Songs");
        }

        if (string.IsNullOrEmpty(home))
        {
            yield break;
        }

        // 3. Conventions Linux/XDG.
        yield return Path.Combine(home, "Clone Hero", "Songs");
        yield return Path.Combine(home, ".local", "share", "Clone Hero", "Songs");
        yield return Path.Combine(home, "Music", "Clone Hero", "Songs");
        yield return Path.Combine(home, "Musique", "Clone Hero", "Songs");

        // 4. Préfixe Proton : quand le build Windows tourne sous Steam Play,
        //    « Mes documents » vit dans le prefix Wine du jeu. On remonte
        //    depuis le dossier du jeu plutôt que de deviner l'AppID.
        foreach (var path in ProtonPrefixCandidates())
        {
            yield return path;
        }
    }

    /// .../steamapps/common/Clone Hero  ->  .../steamapps/compatdata/*/pfx/
    /// drive_c/users/steamuser/Documents/Clone Hero/Songs
    private static IEnumerable<string> ProtonPrefixCandidates()
    {
        string? steamapps = null;

        try
        {
            var dir = new DirectoryInfo(Paths.GameRootPath);
            while (dir != null)
            {
                if (string.Equals(dir.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
                {
                    steamapps = dir.FullName;
                    break;
                }

                dir = dir.Parent;
            }
        }
        catch (Exception)
        {
            yield break;
        }

        if (steamapps == null)
        {
            yield break;
        }

        var compatdata = Path.Combine(steamapps, "compatdata");
        string[] prefixes;

        try
        {
            prefixes = Directory.Exists(compatdata)
                ? Directory.GetDirectories(compatdata)
                : Array.Empty<string>();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var prefix in prefixes)
        {
            yield return Path.Combine(
                prefix,
                "pfx",
                "drive_c",
                "users",
                "steamuser",
                "Documents",
                "Clone Hero",
                "Songs"
            );
        }
    }
}
