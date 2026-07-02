using MultiplayerChat.Settings;

namespace MultiplayerChat.Core;

internal static class MpCustomAvatarUserNotifier
{
    private const float AvatarStatusMessageLifetimeSeconds = 5f;

    public static void PostDownloading(string ownerUserId, string playerDisplayName)
    {
        Post(ownerUserId,
            $"Downloading {FormatPlayerName(playerDisplayName)}'s avatar. Please wait.");
    }

    private static void Post(string ownerUserId, string message)
    {
        if (!ModSettings.ShowSystemMessages)
            return;
        if (string.IsNullOrEmpty(ownerUserId))
            return;

        var chat = ChatManager.Instance;
        if (chat == null)
            return;

        chat.PostSystemMessage(message);
        MpChatLobbyAvatarLifecycleHost.ScheduleSystemMessageRemoval(
            message,
            AvatarStatusMessageLifetimeSeconds);
    }

    private static string FormatPlayerName(string? name)
    {
        var n = (name ?? "").Trim();
        return string.IsNullOrEmpty(n) ? "Player" : n;
    }
}
