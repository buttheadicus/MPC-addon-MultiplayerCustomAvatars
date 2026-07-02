using UnityEngine;

namespace MultiplayerChat.Core;

// Arena spawn timing: intro/countdown is not active beatmap gameplay even when spawn controllers exist.
internal static class MpChatArenaTiming
{
    private const float ArenaSpawnDuringSongIntervalSeconds = 2f;

    private const float SongIntroEndSeconds = 0.05f;

    private static float _lastArenaSpawnAttemptRealtime = -999f;

    private static AudioTimeSyncController? _cachedAtsc;
    private static int _cachedAtscSceneHandle = int.MinValue;

    internal static bool ShouldDeferArenaAvatarSpawn()
    {
        if (!MpChatLobbyDiagnostics.AnyGameCoreLoaded())
            return false;

        if (!SongTimePastIntro())
            return false;

        return Time.realtimeSinceStartup - _lastArenaSpawnAttemptRealtime <
               ArenaSpawnDuringSongIntervalSeconds;
    }

    internal static void NotifyArenaSpawnAttempt() =>
        _lastArenaSpawnAttemptRealtime = Time.realtimeSinceStartup;

    private static bool SongTimePastIntro()
    {
        try
        {
            var atsc = ResolveAudioTimeSyncController();
            return atsc != null && atsc.isActiveAndEnabled && atsc.songTime > SongIntroEndSeconds;
        }
        catch
        {
            return false;
        }
    }

    private static AudioTimeSyncController? ResolveAudioTimeSyncController()
    {
        var sceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        if (_cachedAtsc == null || _cachedAtscSceneHandle != sceneHandle)
        {
            _cachedAtscSceneHandle = sceneHandle;
            _cachedAtsc = Object.FindObjectOfType<AudioTimeSyncController>();
        }

        return _cachedAtsc;
    }
}
