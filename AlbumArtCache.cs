using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace ChorusMod;

/// <summary>
/// Chargement des jaquettes depuis files.enchor.us.
///
/// Le constructeur Texture2D(int, int) n'a pas été restauré par Cpp2IL sur
/// cette build, donc impossible de décoder un JPEG à la main avec
/// ImageConversion.LoadImage. Parade : UnityWebRequestTexture fabrique la
/// texture côté natif — on n'appelle jamais le constructeur interdit.
///
/// Pas de coroutine non plus (encore une API dont on ignore l'état) : les
/// requêtes sont pompées depuis Update().
/// </summary>
public class AlbumArtCache
{
    private const int MaxConcurrent = 4;

    private readonly Dictionary<string, Texture2D> _textures = new();
    private readonly Dictionary<string, UnityWebRequest> _pending = new();
    private readonly HashSet<string> _failed = new();
    private readonly List<string> _completed = new();

    private bool _disabled;
    private bool _loggedFirstCall;
    private int _loggedCompletions;

    public bool Disabled => _disabled;

    /// Retourne la texture si elle est prête, sinon lance le chargement
    /// et retourne null (l'appelant dessine un placeholder).
    public Texture2D? Get(string key, string url)
    {
        if (!_loggedFirstCall)
        {
            _loggedFirstCall = true;
            Plugin.Logger.LogInfo(
                $"Jaquettes — premier appel : key='{key}' url='{url}' "
                    + $"disabled={_disabled}"
            );
        }

        if (_disabled || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(url))
        {
            return null;
        }

        if (_textures.TryGetValue(key, out var texture))
        {
            return texture;
        }

        if (_failed.Contains(key) || _pending.ContainsKey(key))
        {
            return null;
        }

        if (_pending.Count >= MaxConcurrent)
        {
            return null; // on réessaiera à la frame suivante
        }

        try
        {
            var request = UnityWebRequestTexture.GetTexture(url);
            request.SendWebRequest();
            _pending[key] = request;
            Plugin.Logger.LogInfo($"Jaquette demandée : {url}");
        }
        catch (Exception e)
        {
            _disabled = true;
            Plugin.Logger.LogWarning(
                $"Jaquettes indisponibles sur cette build : {e.Message}"
            );
        }

        return null;
    }

    /// À appeler depuis Update() : récupère les requêtes terminées.
    public void Pump()
    {
        if (_disabled || _pending.Count == 0)
        {
            return;
        }

        _completed.Clear();

        foreach (var entry in _pending)
        {
            if (entry.Value == null || entry.Value.isDone)
            {
                _completed.Add(entry.Key);
            }
        }

        foreach (var key in _completed)
        {
            var request = _pending[key];
            _pending.Remove(key);

            try
            {
                var texture = DownloadHandlerTexture.GetContent(request);

                if (_loggedCompletions < 3)
                {
                    _loggedCompletions++;
                    Plugin.Logger.LogInfo(
                        $"Jaquette terminée : code={request.responseCode} "
                            + $"erreur='{request.error}' texture={(texture == null ? "null" : $"{texture.width}x{texture.height}")}"
                    );
                }

                if (texture != null)
                {
                    _textures[key] = texture;
                }
                else
                {
                    _failed.Add(key);
                }
            }
            catch (Exception e)
            {
                _failed.Add(key);
                Plugin.Logger.LogWarning($"Jaquette non décodée : {e.Message}");
            }
            finally
            {
                try
                {
                    request?.Dispose();
                }
                catch (Exception)
                {
                    // Rien à faire.
                }
            }
        }
    }
}
