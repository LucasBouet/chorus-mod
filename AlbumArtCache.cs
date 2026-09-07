using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace ChorusMod;

/// <summary>
/// Loads album art from files.enchor.us.
///
/// The Texture2D(int, int) constructor wasn't restored by Cpp2IL on this
/// build, so decoding a JPEG by hand with ImageConversion.LoadImage is
/// impossible. Workaround: UnityWebRequestTexture builds the texture
/// natively — the forbidden constructor is never called.
///
/// No coroutines either (yet another API of unknown availability): requests
/// are pumped from Update().
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

    /// Returns the texture if ready, otherwise starts loading it and
    /// returns null (the caller draws a placeholder).
    public Texture2D? Get(string key, string url)
    {
        if (!_loggedFirstCall)
        {
            _loggedFirstCall = true;
            Plugin.Logger.LogInfo(
                $"Album art — first call: key='{key}' url='{url}' "
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
            return null; // we'll retry next frame
        }

        try
        {
            var request = UnityWebRequestTexture.GetTexture(url);
            request.SendWebRequest();
            _pending[key] = request;
            Plugin.Logger.LogInfo($"Album art requested: {url}");
        }
        catch (Exception e)
        {
            _disabled = true;
            Plugin.Logger.LogWarning(
                $"Album art unavailable on this build: {e.Message}"
            );
        }

        return null;
    }

    /// Call from Update(): collects completed requests.
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
                        $"Album art completed: code={request.responseCode} "
                            + $"error='{request.error}' texture={(texture == null ? "null" : $"{texture.width}x{texture.height}")}"
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
                Plugin.Logger.LogWarning($"Album art decode failed: {e.Message}");
            }
            finally
            {
                try
                {
                    request?.Dispose();
                }
                catch (Exception)
                {
                    // Nothing to do.
                }
            }
        }
    }
}
