using MultiplayerChat.Settings;
using UnityEngine;

namespace MultiplayerChat.Core;

// scaleAnimator hide shrinks pedestals to ~0.05; keep vanilla BeatAvatars visible until a custom spawn exists.
public sealed class MpChatLobbyPedestalScaleGuard : MonoBehaviour
{
    private void LateUpdate()
    {
        if (!MpChatFeatures.LobbyCustomAvatars || !ModSettings.EnableLobbyCustomAvatars)
            return;

        var driver = GetComponent<MpChatLobbyCustomAvatarDriver>();
        if (driver != null && driver.HasActiveCustomAvatar)
            return;

        if (transform.localScale.x < 0.5f)
            transform.localScale = Vector3.one;
    }
}
