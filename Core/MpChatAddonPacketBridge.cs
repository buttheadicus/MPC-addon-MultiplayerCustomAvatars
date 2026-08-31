using System;
using MultiplayerChat.Core.Addons;
using UnityEngine;

namespace MultiplayerChat.Core;

// mpPacketSerializer callbacks are detached when GameCore ChatManager disposes; re-attach on lobby return.
internal static class MpChatAddonPacketBridge
{
    private static ChatManager? _attachedForChatManager;
    private static float _lastAttachRealtime = -999f;

    internal static void ReattachCallbacks(bool force = false)
    {
        var chatManager = ChatManager.Instance;
        var serializer = chatManager?.PacketSerializer;
        if (serializer == null)
            return;

        if (!force &&
            ReferenceEquals(_attachedForChatManager, chatManager) &&
            Time.realtimeSinceStartup - _lastAttachRealtime < 2f)
            return;

        try
        {
            AddonPacketSerializerBridge.Attach(serializer);
            _attachedForChatManager = chatManager;
            _lastAttachRealtime = Time.realtimeSinceStartup;
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Reattach packet callbacks failed: {ex.Message}");
        }
    }
}
