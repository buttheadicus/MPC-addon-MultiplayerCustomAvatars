using System;
using System.Reflection;
using BeatSaber.AvatarCore;
using CustomAvatar.Avatar;
using CustomAvatar.Player;
using MultiplayerCore.Models;
using MultiplayerCore.Networking;
using UnityEngine;
using Zenject;

namespace MultiplayerChat.Core;

// Facade-mounted drivers are outside the pose Zenject subgraph; fill deps manually when Inject fails.
internal static class MpChatArenaDependencyResolver
{
    internal static bool TryFill(MpChatLobbyCustomAvatarDriver driver, Transform facadeRoot)
    {
        if (driver == null || facadeRoot == null)
            return false;

        var pose = MpChatArenaAvatarAttach.SelectArenaPose(facadeRoot);
        if (pose == null)
            return false;

        driver.SyncArenaPose(pose);

        if (!TryGetConnectedPlayer(pose, out var connectedPlayer))
            return false;

        var playerContainer = MpChatArenaFacadeRoots.FindPlayerContext(facadeRoot)?.Container;

        if (!TryResolve(playerContainer, out AvatarSpawner? spawner) || spawner == null)
            return false;
        if (!TryResolve(playerContainer, out AvatarLoader? loader) || loader == null)
            return false;
        if (!TryResolve(playerContainer, out IMultiplayerSessionManager? session) || session == null)
            return false;

        driver.ApplyResolvedDependencies(spawner, loader, connectedPlayer, pose, session);
        return true;
    }

    private static bool TryGetConnectedPlayer(
        MultiplayerAvatarPoseController pose,
        out IConnectedPlayer connectedPlayer)
    {
        connectedPlayer = null!;

        var field = typeof(MultiplayerAvatarPoseController).GetField(
            "_connectedPlayer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(pose) is IConnectedPlayer fromField)
        {
            connectedPlayer = fromField;
            return true;
        }

        return false;
    }

    private static bool TryResolve<T>(DiContainer? playerContainer, out T? value) where T : class
    {
        value = null;

        if (TryResolveFromContainer(playerContainer, out value))
            return true;

        if (TryResolveFromContainer(ProjectContext.Instance?.Container, out value))
            return true;

        foreach (var sceneContext in UnityEngine.Object.FindObjectsOfType<SceneContext>(true))
        {
            if (TryResolveFromContainer(sceneContext.Container, out value))
                return true;
        }

        return false;
    }

    private static bool TryResolveFromContainer<T>(DiContainer? container, out T? value) where T : class
    {
        value = null;
        if (container == null)
            return false;

        try
        {
            value = container.Resolve<T>();
            return value != null;
        }
        catch (Exception ex) when (ex is ZenjectException or InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}
