using System.Collections.Generic;
using BeatSaber.AvatarCore;
using UnityEngine;

namespace MultiplayerChat.Core;

internal static class MpChatArenaAvatarAttach
{
    internal static void ScanGameCoreAvatars()
    {
        if (!CustomAvatarDependenciesBootstrap.IsSessionActive())
            return;
        if (!MpChatFeatures.LobbyCustomAvatarsInArena)
            return;

        var refreshed = new HashSet<int>();
        foreach (var facade in Object.FindObjectsOfType<MultiplayerConnectedPlayerFacade>(true))
        {
            if (facade == null || IsRedecoratorShadowFacade(facade))
                continue;
            refreshed.Add(facade.gameObject.GetInstanceID());
            RefreshAttachForFacadeRoot(facade.transform);
        }

    }

    internal static void RefreshAttachForGameplay(MultiplayerConnectedPlayerFacade facade) =>
        RefreshAttachForFacadeRoot(facade != null ? facade.transform : null);

    internal static void RefreshAttachForFacadeRoot(Transform? facadeRoot)
    {
        if (!CustomAvatarDependenciesBootstrap.IsSessionActive())
            return;
        if (!MpChatFeatures.LobbyCustomAvatarsInArena)
            return;
        if (facadeRoot == null || MpChatArenaFacadeRoots.IsRedecoratorShadow(facadeRoot))
            return;

        // Local duel rig: other clients render your custom avatar on their connected-player facade.
        if (facadeRoot.GetComponent<MultiplayerLocalActivePlayerFacade>() != null)
            return;

        var pose = SelectArenaPose(facadeRoot);
        if (pose == null)
        {
            MultiplayerChat.Plugin.Log?.Warn(
                $"[MPChat][LobbyAvatar] Arena attach: no platform pose under {facadeRoot.name}");
            return;
        }

        var driver = EnsurePoseDriver(pose, facadeRoot);
        if (driver == null)
            return;

        RemoveStaleDrivers(facadeRoot, pose, driver);
        driver.SyncArenaPose(pose);
        MpChatLobbyAvatarZenject.TryInjectFromFacadeRoot(facadeRoot, driver);
        MpChatArenaDependencyResolver.TryFill(driver, facadeRoot);
        driver.TryBeginStartup();
        driver.PromoteArenaAfterIntro();
        driver.KickArenaFromRemoteSync();
    }

    internal static void TryAttachToPose(MultiplayerAvatarPoseController pose)
    {
        if (!CustomAvatarDependenciesBootstrap.IsSessionActive())
            return;
        if (!MpChatFeatures.LobbyCustomAvatarsInArena)
            return;
        if (!string.Equals(pose.gameObject.scene.name, "GameCore", System.StringComparison.Ordinal))
            return;
        if (pose.GetComponentInParent<MultiplayerLobbyAvatarController>() != null)
            return;
        if (IsUnderBigAvatarIntro(pose.transform))
            return;

        var facadeRoot = MpChatArenaFacadeRoots.FindFrom(pose.transform);
        if (facadeRoot == null || MpChatArenaFacadeRoots.IsRedecoratorShadow(facadeRoot))
            return;

        RefreshAttachForFacadeRoot(facadeRoot);
    }

    internal static bool CanAttemptArenaSpawn(MultiplayerAvatarPoseController? pose)
    {
        if (pose == null)
            return false;
        if (IsUnderBigAvatarIntro(pose.transform))
            return false;
        if (pose.GetComponentInParent<MultiplayerLobbyAvatarController>() != null)
            return false;
        return MpChatArenaFacadeRoots.HasArenaFacade(pose.transform);
    }

    internal static bool IsArenaPoseReadyForCustomSpawn(MultiplayerAvatarPoseController? pose)
    {
        if (!CanAttemptArenaSpawn(pose))
            return false;
        return pose!.gameObject.activeInHierarchy;
    }

    internal static MultiplayerAvatarPoseController? SelectArenaPose(Transform facadeRoot)
    {
        MultiplayerAvatarPoseController? inactiveGameplayPose = null;
        MultiplayerAvatarPoseController? inactivePlatformPose = null;
        MultiplayerAvatarPoseController? activePlatformPose = null;

        foreach (var pose in facadeRoot.GetComponentsInChildren<MultiplayerAvatarPoseController>(true))
        {
            if (pose.GetComponentInParent<MultiplayerLobbyAvatarController>() != null)
                continue;
            if (IsUnderBigAvatarIntro(pose.transform))
                continue;

            if (PoseLooksLikeGameplayAvatar(pose.transform))
            {
                if (pose.gameObject.activeInHierarchy)
                    return pose;
                inactiveGameplayPose ??= pose;
                continue;
            }

            if (pose.gameObject.activeInHierarchy)
                activePlatformPose = pose;
            else
                inactivePlatformPose ??= pose;
        }

        return inactiveGameplayPose ?? activePlatformPose ?? inactivePlatformPose;
    }

    internal static MultiplayerAvatarPoseController? SelectArenaPose(MultiplayerConnectedPlayerFacade facade) =>
        facade != null ? SelectArenaPose(facade.transform) : null;

    internal static Transform GetArenaSpawnParent(Transform facadeRoot, MultiplayerAvatarPoseController pose)
    {
        if (IsArenaPoseReadyForCustomSpawn(pose))
            return pose.transform;

        var anchor = facadeRoot.Find("MpChatArenaCustomAvatarAnchor");
        if (anchor == null)
        {
            var go = new GameObject("MpChatArenaCustomAvatarAnchor");
            anchor = go.transform;
            anchor.SetParent(facadeRoot, false);
            anchor.localPosition = Vector3.zero;
            anchor.localRotation = Quaternion.identity;
            anchor.localScale = Vector3.one;
            MpChatArenaAnchorRegistry.Register(anchor);
        }

        return anchor;
    }

    internal static Transform GetArenaSpawnParent(
        MultiplayerConnectedPlayerFacade facade,
        MultiplayerAvatarPoseController pose) =>
        facade != null ? GetArenaSpawnParent(facade.transform, pose) : pose.transform;

    internal static bool IsRedecoratorShadowFacade(MultiplayerConnectedPlayerFacade facade) =>
        facade == null || MpChatArenaFacadeRoots.IsRedecoratorShadow(facade.transform);

    private static MpChatLobbyCustomAvatarDriver? EnsurePoseDriver(
        MultiplayerAvatarPoseController pose,
        Transform facadeRoot)
    {
        var go = pose.gameObject;
        var driver = go.GetComponent<MpChatLobbyCustomAvatarDriver>();
        if (driver == null)
        {
            driver = go.AddComponent<MpChatLobbyCustomAvatarDriver>();
            MultiplayerChat.Plugin.Log?.Debug(
                $"[MPChat][LobbyAvatar] Arena driver attached on {GetTransformPath(pose.transform)} ({facadeRoot.name})");
        }

        return driver;
    }

    private static void RemoveStaleDrivers(
        Transform facadeRoot,
        MultiplayerAvatarPoseController keepPose,
        MpChatLobbyCustomAvatarDriver keepDriver)
    {
        foreach (var driver in facadeRoot.GetComponentsInChildren<MpChatLobbyCustomAvatarDriver>(true))
        {
            if (driver == null || driver == keepDriver)
                continue;

            driver.HandOffSpawnTo(keepDriver);
            Object.Destroy(driver);
        }

        var facadeDriver = facadeRoot.GetComponent<MpChatLobbyCustomAvatarDriver>();
        if (facadeDriver != null && facadeDriver != keepDriver)
        {
            facadeDriver.HandOffSpawnTo(keepDriver);
            Object.Destroy(facadeDriver);
        }
    }

    internal static void DestroyOrphanedArenaObjects() => MpChatArenaAnchorRegistry.DestroyAll();

    internal static bool IsUnderBigAvatarIntro(Transform poseRoot) =>
        poseRoot.GetComponentInParent<MultiplayerBigAvatarAnimator>() != null;

    private static bool PoseLooksLikeGameplayAvatar(Transform poseRoot)
    {
        for (var cur = poseRoot; cur != null; cur = cur.parent)
        {
            var name = cur.name;
            if (name.IndexOf("MultiplayerGameAvatar", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (name.IndexOf("LocalPlayerGameCore", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (name.IndexOf("RemotePlayerGameCore", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        var rootName = poseRoot.name;
        if (rootName.IndexOf("MultiplayerGameAvatar", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (rootName.IndexOf("LocalPlayerGameCore", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (rootName.IndexOf("RemotePlayerGameCore", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        return poseRoot.Find("SaberL") != null || poseRoot.Find("SaberR") != null;
    }

    private static string GetTransformPath(Transform t)
    {
        var path = t.name;
        while (t.parent != null)
        {
            t = t.parent;
            path = t.name + "/" + path;
        }

        return path;
    }
}
