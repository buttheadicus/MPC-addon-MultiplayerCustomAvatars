using System;
using System.Collections.Generic;

namespace MultiplayerChat.Core;

// Indexed lobby drivers so player join/disconnect never scans the whole scene.
internal static class MpChatLobbyCustomAvatarDriverRegistry
{
    private static readonly List<MpChatLobbyCustomAvatarDriver> AllDrivers = new(16);

    private static readonly Dictionary<string, List<MpChatLobbyCustomAvatarDriver>> ByUserId =
        new(StringComparer.Ordinal);

    private static readonly List<MpChatLobbyCustomAvatarDriver> MirrorDrivers = new(4);

    internal static void Register(MpChatLobbyCustomAvatarDriver driver)
    {
        if (driver == null)
            return;

        if (!AllDrivers.Contains(driver))
            AllDrivers.Add(driver);

        IndexDriver(driver);
        OnRemoteLobbyPedestalRegistered(driver);
    }

    // Pedestals often appear after playerConnected; retry join work once the driver exists.
    private static void OnRemoteLobbyPedestalRegistered(MpChatLobbyCustomAvatarDriver driver)
    {
        if (!driver.isActiveAndEnabled)
            return;

        if (driver.IsArenaContextForRegistry())
        {
            driver.KickArenaFromRemoteSync();
            return;
        }

        if (driver.IsMirrorPedestalForRegistry())
            return;

        var userId = driver.RegistryUserId;
        if (string.IsNullOrEmpty(userId))
            return;

        MpCustomAvatarSyncManager.NotifyRemoteAvatarMayBeReady(userId);
    }

    internal static void Unregister(MpChatLobbyCustomAvatarDriver driver)
    {
        if (driver == null)
            return;

        AllDrivers.Remove(driver);
        MirrorDrivers.Remove(driver);
        RemoveFromUserIndex(driver.RegistryUserId, driver);
    }

    internal static void Reindex(MpChatLobbyCustomAvatarDriver driver, string? previousUserId)
    {
        if (driver == null)
            return;

        if (!string.IsNullOrEmpty(previousUserId))
            RemoveFromUserIndex(previousUserId, driver);

        IndexDriver(driver);
    }

    internal static void ForUser(string userId, Action<MpChatLobbyCustomAvatarDriver> action, bool lobbyPedestalsOnly)
    {
        if (string.IsNullOrEmpty(userId) || !ByUserId.TryGetValue(userId, out var list))
            return;

        for (var i = 0; i < list.Count; i++)
        {
            var driver = list[i];
            if (driver == null)
                continue;
            if (lobbyPedestalsOnly && driver.IsArenaContextForRegistry())
                continue;
            action(driver);
        }
    }

    internal static void ForAll(Action<MpChatLobbyCustomAvatarDriver> action)
    {
        for (var i = 0; i < AllDrivers.Count; i++)
        {
            var driver = AllDrivers[i];
            if (driver != null)
                action(driver);
        }
    }

    internal static void ForAllLobbyPedestals(Action<MpChatLobbyCustomAvatarDriver> action)
    {
        for (var i = 0; i < AllDrivers.Count; i++)
        {
            var driver = AllDrivers[i];
            if (driver == null || !driver.isActiveAndEnabled || driver.IsArenaContextForRegistry())
                continue;
            action(driver);
        }
    }

    internal static void CollectLobbyPedestals(List<MpChatLobbyCustomAvatarDriver> into)
    {
        into.Clear();
        for (var i = 0; i < AllDrivers.Count; i++)
        {
            var driver = AllDrivers[i];
            if (driver == null || !driver.isActiveAndEnabled || driver.IsArenaContextForRegistry())
                continue;

            // Mirror preview last: only one concurrent lobby load slot; remotes must not stay pending behind mirror.
            if (driver.IsMirrorPedestalForRegistry())
                continue;

            into.Add(driver);
        }

        for (var i = 0; i < AllDrivers.Count; i++)
        {
            var driver = AllDrivers[i];
            if (driver == null || !driver.isActiveAndEnabled || driver.IsArenaContextForRegistry())
                continue;
            if (!driver.IsMirrorPedestalForRegistry())
                continue;

            into.Add(driver);
        }
    }

    internal static void WakePendingArenaLoads()
    {
        for (var i = 0; i < AllDrivers.Count; i++)
        {
            var driver = AllDrivers[i];
            if (driver == null || !driver.isActiveAndEnabled || !driver.IsArenaContextForRegistry())
                continue;

            driver.TryResumePendingLoad();
        }
    }

    internal static void WakePendingLobbyLoads()
    {
        for (var i = 0; i < AllDrivers.Count; i++)
        {
            var driver = AllDrivers[i];
            if (driver == null || !driver.isActiveAndEnabled || driver.IsArenaContextForRegistry())
                continue;

            driver.TryResumePendingLoad();
        }
    }

    internal static void ForMirrorDrivers(Action<MpChatLobbyCustomAvatarDriver> action)
    {
        for (var i = 0; i < MirrorDrivers.Count; i++)
        {
            var driver = MirrorDrivers[i];
            if (driver != null)
                action(driver);
        }
    }

    internal static int ActiveLobbyPedestalDriverCount()
    {
        var count = 0;
        for (var i = 0; i < AllDrivers.Count; i++)
        {
            var driver = AllDrivers[i];
            if (driver != null && driver.isActiveAndEnabled && !driver.IsArenaContextForRegistry())
                count++;
        }

        return count;
    }

    internal static float GetLobbyVisualMaintainIntervalSeconds()
    {
        var n = ActiveLobbyPedestalDriverCount();
        if (n <= 3)
            return 0.25f;
        if (n <= 6)
            return 0.5f;
        if (n <= 10)
            return 0.85f;
        return 1.25f;
    }

    internal static float GetLobbySpawnRetryIntervalSeconds()
    {
        var n = ActiveLobbyPedestalDriverCount();
        if (n <= 4)
            return 0.5f;
        if (n <= 8)
            return 0.85f;
        return 1.25f;
    }

    private static void IndexDriver(MpChatLobbyCustomAvatarDriver driver)
    {
        var userId = driver.RegistryUserId;
        if (!string.IsNullOrEmpty(userId))
        {
            if (!ByUserId.TryGetValue(userId, out var list))
            {
                list = new List<MpChatLobbyCustomAvatarDriver>(2);
                ByUserId[userId] = list;
            }

            if (!list.Contains(driver))
                list.Add(driver);
        }

        if (driver.IsMirrorPedestalForRegistry())
        {
            if (!MirrorDrivers.Contains(driver))
                MirrorDrivers.Add(driver);
        }
        else
        {
            MirrorDrivers.Remove(driver);
        }
    }

    private static void RemoveFromUserIndex(string? userId, MpChatLobbyCustomAvatarDriver driver)
    {
        if (userId is not { Length: > 0 } key)
            return;

        if (!ByUserId.TryGetValue(key, out var list))
            return;

        list.Remove(driver);
        if (list.Count == 0)
            ByUserId.Remove(key);
    }
}
