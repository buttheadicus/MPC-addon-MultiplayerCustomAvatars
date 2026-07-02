using System;
using HarmonyLib;

namespace MultiplayerChat.HarmonyPatches;

internal static class MpChatArenaAvatarHarmony
{
    internal static void Apply(Harmony harmony)
    {
        TryPatch(harmony, typeof(MpChatArenaAvatarPoseStartPatch));
        TryPatch(harmony, typeof(MpChatArenaAvatarPoseConnectedPatch));
        TryPatch(harmony, typeof(MpChatArenaFacadeHideBigAvatarPatch));
    }

    private static void TryPatch(Harmony harmony, Type patchType)
    {
        try
        {
            harmony.CreateClassProcessor(patchType).Patch();
        }
        catch (Exception ex)
        {
            Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Arena Harmony patch {patchType.Name} failed: {ex.Message}");
        }
    }
}
