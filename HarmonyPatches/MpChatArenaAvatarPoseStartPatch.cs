using System.Reflection;
using BeatSaber.AvatarCore;
using HarmonyLib;
using MultiplayerChat.Core;

namespace MultiplayerChat.HarmonyPatches;

[HarmonyPatch]
internal static class MpChatArenaAvatarPoseStartPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(MultiplayerAvatarPoseController), "Start")!;

    private static void Postfix(MultiplayerAvatarPoseController __instance)
    {
        if (!CustomAvatarDependenciesBootstrap.IsSessionActive())
            return;
        if (!MpChatFeatures.LobbyCustomAvatarsInArena)
            return;
        if (!string.Equals(__instance.gameObject.scene.name, "GameCore", System.StringComparison.Ordinal))
            return;
        if (__instance.GetComponentInParent<MultiplayerLobbyAvatarController>() != null)
            return;
        if (MpChatArenaAvatarAttach.IsUnderBigAvatarIntro(__instance.transform))
            return;

        MpChatArenaAvatarAttach.TryAttachToPose(__instance);
    }
}
