using MultiplayerChat.Settings;
using UnityEngine;

namespace MultiplayerChat.Core;

internal static class MpChatAvatarWorkloadGate
{
    private static int _gameCoreFrame = -1;
    private static bool _gameCoreLoaded;

    // sceneCount walk once per frame so lobby LateUpdate/ScaleGuard do not repeat it.
    internal static bool GameCoreLoadedThisFrame
    {
        get
        {
            var frame = Time.frameCount;
            if (frame != _gameCoreFrame)
            {
                _gameCoreFrame = frame;
                _gameCoreLoaded = MpChatLobbyDiagnostics.AnyGameCoreLoaded();
            }

            return _gameCoreLoaded;
        }
    }

    // song, countdown, results, and GameCore handoff: no avatar file or spawn work. so its gated it here.
    internal static bool ShouldDeferAvatarNetworkDiskAndSpawnWork =>
        GameCoreLoadedThisFrame ||
        MpChatPerformanceGate.IsMultiplayerSceneTransitionLikely();

    internal static bool ShouldDeferArenaAvatarSpawn =>
        MpChatArenaTiming.ShouldDeferArenaAvatarSpawn();

    internal static bool CanRunLobbyAvatarFileWork =>
        MpChatFeatures.LobbyCustomAvatars &&
        ModSettings.EnableLobbyCustomAvatars &&
        !ShouldDeferAvatarNetworkDiskAndSpawnWork &&
        MpChatLobbyDiagnostics.LobbyHierarchyLooksLikeMultiplayerLobby() &&
        !MpChatPerformanceGate.ShouldBlockAvatarHeavyWork;
}
