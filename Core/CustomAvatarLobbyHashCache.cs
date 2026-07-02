using System;
using System.Collections.Generic;
using System.IO;
using MultiplayerChat.Settings;

namespace MultiplayerChat.Core;

internal static class CustomAvatarLobbyHashCache
{
    private static readonly object Gate = new();

    private static Dictionary<string, string>? _hashToPath;

    internal static void Invalidate()
    {
        lock (Gate)
            _hashToPath = null;
    }

    internal static void RegisterLobbyCacheFile(string md5HexUpper)
    {
        md5HexUpper = md5HexUpper.Trim().ToUpperInvariant();
        if (!CustomAvatarHashUtil.LooksLikeMd5Hex(md5HexUpper))
            return;

        RememberPath(md5HexUpper, CustomAvatarLobbyCachePaths.PathForHash(md5HexUpper));
    }

    internal static bool TryGetPath(string md5HexUpper, out string fullPath)
    {
        fullPath = "";
        if (!CustomAvatarHashUtil.LooksLikeMd5Hex(md5HexUpper))
            return false;

        md5HexUpper = md5HexUpper.ToUpperInvariant();

        var cachedPath = CustomAvatarLobbyCachePaths.PathForHash(md5HexUpper);
        if (File.Exists(cachedPath))
        {
            fullPath = cachedPath;
            RegisterLobbyCacheFile(md5HexUpper);
            return true;
        }

        lock (Gate)
        {
            if (_hashToPath != null &&
                _hashToPath.TryGetValue(md5HexUpper, out fullPath) &&
                File.Exists(fullPath))
                return true;
        }

        if (TryResolveConfiguredLocalAvatar(md5HexUpper, out fullPath))
            return true;

        if (TryResolveHashNamedUserAvatar(md5HexUpper, out fullPath))
            return true;

        return false;
    }

    private static bool TryResolveConfiguredLocalAvatar(string md5HexUpper, out string fullPath)
    {
        fullPath = "";
        var rel = ModSettings.LobbyCustomAvatarRelativePath.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(rel))
            return false;

        var candidate = Path.Combine(
            BeatSaberPaths.CustomAvatarsDirectory,
            rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(candidate))
            return false;

        try
        {
            if (!string.Equals(CustomAvatarHashUtil.Md5HexFile(candidate), md5HexUpper, StringComparison.OrdinalIgnoreCase))
                return false;

            fullPath = candidate;
            RememberPath(md5HexUpper, candidate);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveHashNamedUserAvatar(string md5HexUpper, out string fullPath)
    {
        fullPath = "";
        var dir = BeatSaberPaths.CustomAvatarsDirectory;
        if (!Directory.Exists(dir))
            return false;

        var candidate = Path.Combine(dir, md5HexUpper + ".avatar");
        if (!File.Exists(candidate))
            return false;

        fullPath = candidate;
        RememberPath(md5HexUpper, candidate);
        return true;
    }

    private static void RememberPath(string md5HexUpper, string file)
    {
        lock (Gate)
        {
            _hashToPath ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!_hashToPath.ContainsKey(md5HexUpper))
                _hashToPath[md5HexUpper] = file;
        }
    }
}
