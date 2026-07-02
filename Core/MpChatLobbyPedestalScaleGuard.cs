using MultiplayerChat.Settings;
using MultiplayerCore.Models;
using MultiplayerCore.Networking;
using UnityEngine;
using Zenject;

namespace MultiplayerChat.Core;

// ScaleAnimator patches shrink lobby pedestals; local pedestal has no custom driver, so keep it visible.
public sealed class MpChatLobbyPedestalScaleGuard : MonoBehaviour
{
    private IConnectedPlayer _connectedPlayer = null!;
    private IMultiplayerSessionManager _sessionManager = null!;

    [Inject]
    public void Construct(IConnectedPlayer connectedPlayer, IMultiplayerSessionManager sessionManager)
    {
        _connectedPlayer = connectedPlayer;
        _sessionManager = sessionManager;
    }

    private void LateUpdate()
    {
        if (!MpChatFeatures.LobbyCustomAvatars || !ModSettings.EnableLobbyCustomAvatars)
            return;

        var driver = GetComponent<MpChatLobbyCustomAvatarDriver>();
        if (driver != null && driver.HasActiveCustomAvatar)
            return;

        var lp = _sessionManager.localPlayer;
        if (lp == null || lp.userId != _connectedPlayer.userId)
            return;

        if (transform.localScale.x < 0.5f)
            transform.localScale = Vector3.one;
    }
}
