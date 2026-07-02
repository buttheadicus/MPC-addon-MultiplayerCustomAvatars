using System;
using MultiplayerChat.Core;
using MultiplayerChat.Core.Addons;

namespace MultiplayerChat.Core;

// MpPacketSerializer callbacks are detached when GameCore ChatManager disposes; re-attach on lobby return.
internal static class MpChatAddonPacketBridge
{
    internal static void ReattachCallbacks()
    {
        var serializer = ChatManager.Instance?.PacketSerializer;
        if (serializer == null)
            return;

        try
        {
            AddonPacketSerializerBridge.Attach(serializer);
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Reattach packet callbacks failed: {ex.Message}");
        }
    }
}
