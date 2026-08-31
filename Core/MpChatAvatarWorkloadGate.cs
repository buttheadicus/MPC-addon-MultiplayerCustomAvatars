using MultiplayerChat.Settings;

namespace MultiplayerChat.Core;

internal static class MpChatAvatarWorkloadGate
{
    // song, countdown, results, and GameCore handoff: no avatar file or spawn work. so its gated it here.
    internal static bool ShouldDeferAvatarNetworkDiskAndSpawnWork =>
        MpChatLobbyDiagnostics.AnyGameCoreLoaded() ||
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
