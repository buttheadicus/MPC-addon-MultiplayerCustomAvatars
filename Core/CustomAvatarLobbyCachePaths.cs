using System.IO;

namespace MultiplayerChat.Core;

internal static class CustomAvatarLobbyCachePaths
{
    internal static string CacheDirectory =>
        Path.Combine(BeatSaberPaths.CustomAvatarsDirectory, ".mpchat_cache");

    internal static string PathForHash(string md5HexUpper) =>
        Path.Combine(CacheDirectory, md5HexUpper.ToUpperInvariant() + ".avatar");
}
