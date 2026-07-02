using BeatSaber.AvatarCore;
using MultiplayerChat.Settings;
using UnityEngine;

namespace MultiplayerChat.Core;

// MP results screen reuses lobby avatar pedestals under GameCore; they are not lobby-installer decorated.
internal static class MpChatResultsPedestalAttach
{
    private static float _nextScanRealtime = -999f;

    private const float ScanIntervalSeconds = 0.45f;

    internal static void ScanResultsPedestals(bool force = false)
    {
        if (!CustomAvatarDependenciesBootstrap.IsSessionActive())
            return;
        if (!MpChatFeatures.LobbyCustomAvatars || !ModSettings.EnableLobbyCustomAvatars)
            return;
        if (!MpChatLobbyDiagnostics.ResultsLikeUiVisible())
            return;

        var now = Time.realtimeSinceStartup;
        if (!force && now < _nextScanRealtime)
            return;

        _nextScanRealtime = now + ScanIntervalSeconds;

        foreach (var controller in Object.FindObjectsOfType<MultiplayerLobbyAvatarController>(true))
        {
            if (controller == null || !controller.gameObject.activeInHierarchy)
                continue;

            Decorate(controller);
        }
    }

    private static void Decorate(MultiplayerLobbyAvatarController controller)
    {
        if (controller.GetComponent<MpChatLobbyPedestalScaleGuard>() == null)
            controller.gameObject.AddComponent<MpChatLobbyPedestalScaleGuard>();

        var driver = controller.GetComponent<MpChatLobbyCustomAvatarDriver>();
        if (driver == null)
        {
            driver = controller.gameObject.AddComponent<MpChatLobbyCustomAvatarDriver>();
            MpChatLobbyAvatarZenject.TryInject(driver);
            MultiplayerChat.Plugin.Log?.Debug(
                $"[MPChat][LobbyAvatar] Results pedestal driver attached on {controller.name}");
        }

        driver.TryBeginStartup();
        driver.KickFromRemoteSync();
    }
}
