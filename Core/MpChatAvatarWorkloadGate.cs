using MultiplayerChat.Settings;

namespace MultiplayerChat.Core;

internal static class MpChatAvatarWorkloadGate
{
    // Song, countdown, and GameCore handoff: no avatar file or spawn work (except MP results pedestals).
    internal static bool ShouldDeferAvatarNetworkDiskAndSpawnWork =>
        (MpChatLobbyDiagnostics.AnyGameCoreLoaded() ||
         MpChatPerformanceGate.IsMultiplayerSceneTransitionLikely()) &&
        !MpChatLobbyDiagnostics.ResultsLikeUiVisible();

    internal static bool ShouldDeferArenaAvatarSpawn =>
        MpChatArenaTiming.ShouldDeferArenaAvatarSpawn();

    internal static bool CanRunLobbyAvatarFileWork =>
        MpChatFeatures.LobbyCustomAvatars &&
        ModSettings.EnableLobbyCustomAvatars &&
        !ShouldDeferAvatarNetworkDiskAndSpawnWork &&
        MpChatLobbyDiagnostics.LobbyHierarchyLooksLikeMultiplayerLobby() &&
        !MpChatPerformanceGate.ShouldBlockAvatarHeavyWork;
}
