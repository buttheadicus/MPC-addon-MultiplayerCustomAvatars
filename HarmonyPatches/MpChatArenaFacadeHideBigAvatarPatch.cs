using HarmonyLib;
using MultiplayerChat.Core;
namespace MultiplayerChat.HarmonyPatches;

[HarmonyPatch(typeof(MultiplayerConnectedPlayerFacade), nameof(MultiplayerConnectedPlayerFacade.HideBigAvatar))]
internal static class MpChatArenaFacadeHideBigAvatarPatch
{
    private static void Postfix(MultiplayerConnectedPlayerFacade __instance)
    {
        if (!CustomAvatarDependenciesBootstrap.IsSessionActive())
            return;
        if (!MpChatFeatures.LobbyCustomAvatarsInArena)
            return;

        MpChatArenaAvatarAttach.RefreshAttachForGameplay(__instance);

        foreach (var driver in __instance.GetComponentsInChildren<MpChatLobbyCustomAvatarDriver>(true))
            driver.PromoteArenaAfterIntro();
    }
}
