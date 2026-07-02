using MultiplayerCore.Models;
using UnityEngine;

namespace MultiplayerChat.Core;

internal static class MpChatAddonSessionResolver
{
    internal static IMultiplayerSessionManager? Resolve(IMultiplayerSessionManager? injected) =>
        injected ?? Object.FindObjectOfType<MultiplayerSessionManager>();
}
