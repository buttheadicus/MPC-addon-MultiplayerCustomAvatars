using System.Reflection;
using UnityEngine;
using Zenject;

namespace MultiplayerChat.Core;

// menu Inject has no session; FindObjectOfType misses hidden/disabled BGNet session objects.
internal static class MpChatAddonSessionResolver
{
    private static IMultiplayerSessionManager? _cached;
    private static float _cachedAt;
    private static FieldInfo? _chatManagerSessionField;
    private static bool _loggedResolvedFromChat;
    private static bool _loggedResolvedFromScene;

    internal static IMultiplayerSessionManager? Resolve(IMultiplayerSessionManager? injected)
    {
        var fromChat = TryFromChatManager();
        if (fromChat != null)
        {
            Cache(fromChat);
            if (!_loggedResolvedFromChat)
            {
                _loggedResolvedFromChat = true;
                MultiplayerChat.Plugin.Log?.Debug("[MPChat][LobbyAvatar] using ChatManager session for avatar sync");
            }

            return fromChat;
        }

        if (HasLocalPlayer(injected))
            return injected;

        if (HasLocalPlayer(_cached) && Time.realtimeSinceStartup - _cachedAt < 2f)
            return _cached;

        if (TryFromZenject(out var fromZenject) && fromZenject != null)
        {
            Cache(fromZenject);
            return fromZenject;
        }

        var fromScene = FindInUnity();
        if (fromScene != null)
        {
            Cache(fromScene);
            if (!_loggedResolvedFromScene)
            {
                _loggedResolvedFromScene = true;
                MultiplayerChat.Plugin.Log?.Debug("[MPChat][LobbyAvatar] using scene session for avatar sync");
            }

            return fromScene;
        }

        return injected ?? _cached;
    }

    private static void Cache(IMultiplayerSessionManager session)
    {
        _cached = session;
        _cachedAt = Time.realtimeSinceStartup;
    }

    private static bool HasLocalPlayer(IMultiplayerSessionManager? session) =>
        session?.localPlayer != null && !string.IsNullOrEmpty(session.localPlayer.userId);

    private static IMultiplayerSessionManager? TryFromChatManager()
    {
        var cm = ChatManager.Instance;
        if (cm == null)
            return null;

        try
        {
            _chatManagerSessionField ??= typeof(ChatManager).GetField(
                "_sessionManager",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return _chatManagerSessionField?.GetValue(cm) as IMultiplayerSessionManager;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryFromZenject(out IMultiplayerSessionManager? session)
    {
        session = null;
        if (TryResolve(ProjectContext.Instance?.Container, out session) && session != null)
            return true;

        foreach (var sceneContext in Object.FindObjectsOfType<SceneContext>(true))
        {
            if (TryResolve(sceneContext.Container, out session) && session != null)
                return true;
        }

        return false;
    }

    private static bool TryResolve(DiContainer? container, out IMultiplayerSessionManager? session)
    {
        session = null;
        if (container == null)
            return false;

        try
        {
            session = container.Resolve<IMultiplayerSessionManager>();
            return session != null;
        }
        catch
        {
            return false;
        }
    }

    private static IMultiplayerSessionManager? FindInUnity()
    {
        IMultiplayerSessionManager? withPlayer = null;
        IMultiplayerSessionManager? any = null;

        foreach (var obj in Resources.FindObjectsOfTypeAll(typeof(MultiplayerSessionManager)))
        {
            if (obj is not MultiplayerSessionManager found || found == null)
                continue;

            any ??= found;
            if (found.isActiveAndEnabled && HasLocalPlayer(found))
                return found;
            if (withPlayer == null && HasLocalPlayer(found))
                withPlayer = found;
        }

        return withPlayer ?? any;
    }
}
