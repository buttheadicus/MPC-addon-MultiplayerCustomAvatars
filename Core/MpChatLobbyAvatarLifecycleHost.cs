using System;
using System.Collections;
using System.Collections.Generic;
using MultiplayerChat.Settings;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultiplayerChat.Core;

// Refreshes lobby pedestal custom avatars after arena / GameCore transitions.
public sealed class MpChatLobbyAvatarLifecycleHost : MonoBehaviour
{
    public static MpChatLobbyAvatarLifecycleHost? Instance { get; private set; }

    private Coroutine? _pendingRefresh;

    private static bool _lobbyReturnRefreshInProgress;

    internal static bool IsLobbyReturnRefreshInProgress() => _lobbyReturnRefreshInProgress;

    private Coroutine? _pendingJoinBatch;

    private Coroutine? _pendingLeaveBatch;

    private static readonly List<string> PendingJoinUserIds = new(8);

    private static readonly List<string> PendingLeaveUserIds = new(8);

    private static bool _pendingJoinSendMetadataToPeers;

    private static readonly object PendingJoinLock = new();

    private static readonly object PendingLeaveLock = new();

    private void Awake() => Instance = this;

    public static void QueuePlayerJoinAvatarWork(string userId, bool sendMetadataToJoiner = false)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        lock (PendingJoinLock)
        {
            if (!PendingJoinUserIds.Contains(userId))
                PendingJoinUserIds.Add(userId);
            if (sendMetadataToJoiner)
                _pendingJoinSendMetadataToPeers = true;
        }

        if (Instance != null)
        {
            Instance.EnsureJoinBatchCoroutine();
            return;
        }

        MpChatLobbyCustomAvatarDriver.ProcessPlayerJoinedImmediate(userId);
        if (sendMetadataToJoiner && MpChatFeatures.LobbyCustomAvatars && ModSettings.EnableLobbyCustomAvatars)
            MpCustomAvatarSyncManager.SendLocalMetadataToUser(userId);
    }

    private void EnsureJoinBatchCoroutine()
    {
        if (_pendingJoinBatch != null)
            return;

        _pendingJoinBatch = StartCoroutine(FlushJoinBatchEndOfFrame());
    }

    private IEnumerator FlushJoinBatchEndOfFrame()
    {
        yield return null;

        string[] userIds;
        var sendMetadataToJoiners = false;
        lock (PendingJoinLock)
        {
            if (PendingJoinUserIds.Count == 0)
            {
                _pendingJoinBatch = null;
                yield break;
            }

            userIds = PendingJoinUserIds.ToArray();
            PendingJoinUserIds.Clear();
            sendMetadataToJoiners = _pendingJoinSendMetadataToPeers;
            _pendingJoinSendMetadataToPeers = false;
        }

        for (var i = 0; i < userIds.Length; i++)
        {
            if (!MpChatAvatarWorkloadGate.ShouldDeferAvatarNetworkDiskAndSpawnWork)
                MpChatLobbyCustomAvatarDriver.ProcessPlayerJoinedImmediate(userIds[i]);
            if (userIds.Length > 1 && i < userIds.Length - 1)
                yield return null;
        }

        if (sendMetadataToJoiners && MpChatFeatures.LobbyCustomAvatars && ModSettings.EnableLobbyCustomAvatars)
        {
            for (var i = 0; i < userIds.Length; i++)
                MpCustomAvatarSyncManager.SendLocalMetadataToUser(userIds[i]);
        }

        _pendingJoinBatch = null;
    }

    public static void CancelPendingAvatarWork()
    {
        lock (PendingJoinLock)
        {
            PendingJoinUserIds.Clear();
            _pendingJoinSendMetadataToPeers = false;
        }

        lock (PendingLeaveLock)
            PendingLeaveUserIds.Clear();

        if (Instance == null)
            return;

        if (Instance._pendingJoinBatch != null)
        {
            Instance.StopCoroutine(Instance._pendingJoinBatch);
            Instance._pendingJoinBatch = null;
        }

        if (Instance._pendingLeaveBatch != null)
        {
            Instance.StopCoroutine(Instance._pendingLeaveBatch);
            Instance._pendingLeaveBatch = null;
        }

        if (Instance._pendingRefresh != null)
        {
            Instance.StopCoroutine(Instance._pendingRefresh);
            Instance._pendingRefresh = null;
        }

        _lobbyReturnRefreshInProgress = false;

        if (Instance._pendingArenaScan != null)
        {
            Instance.StopCoroutine(Instance._pendingArenaScan);
            Instance._pendingArenaScan = null;
        }
    }

    public static void QueuePlayerLeaveAvatarWork(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        lock (PendingLeaveLock)
        {
            if (!PendingLeaveUserIds.Contains(userId))
                PendingLeaveUserIds.Add(userId);
        }

        if (Instance != null)
        {
            Instance.EnsureLeaveBatchCoroutine();
            return;
        }

        MpChatLobbyCustomAvatarDriver.ProcessPlayerDisconnectedImmediate(userId);
    }

    private void EnsureLeaveBatchCoroutine()
    {
        if (_pendingLeaveBatch != null)
            return;

        _pendingLeaveBatch = StartCoroutine(FlushLeaveBatchEndOfFrame());
    }

    private IEnumerator FlushLeaveBatchEndOfFrame()
    {
        yield return null;

        string[] userIds;
        lock (PendingLeaveLock)
        {
            if (PendingLeaveUserIds.Count == 0)
            {
                _pendingLeaveBatch = null;
                yield break;
            }

            userIds = PendingLeaveUserIds.ToArray();
            PendingLeaveUserIds.Clear();
        }

        for (var i = 0; i < userIds.Length; i++)
        {
            MpChatLobbyCustomAvatarDriver.ProcessPlayerDisconnectedImmediate(userIds[i]);
            if (userIds.Length > 1 && i < userIds.Length - 1)
                yield return null;
        }

        _pendingLeaveBatch = null;
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(Instance, this))
            Instance = null;
    }

    public static void ScheduleSystemMessageRemoval(string message, float delaySeconds)
    {
        if (string.IsNullOrEmpty(message) || delaySeconds <= 0f)
            return;
        if (Instance == null)
            return;

        Instance.StartCoroutine(RemoveSystemMessageAfter(delaySeconds, message));
    }

    private static IEnumerator RemoveSystemMessageAfter(float delaySeconds, string message)
    {
        yield return new WaitForSeconds(delaySeconds);
        ChatManager.Instance?.RequestRemoveSystemMessage(message);
    }

    private void Update() => MpChatLobbyPosePoll.TickFromHost();

    private void OnEnable()
    {
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    private void OnDisable()
    {
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
    }

    private void OnSceneUnloaded(Scene scene)
    {
        if (string.Equals(scene.name, "GameCore", System.StringComparison.Ordinal) ||
            string.Equals(scene.name, "MultiplayerGameplay", System.StringComparison.Ordinal))
        {
            MpChatArenaAvatarAttach.DestroyOrphanedArenaObjects();
            ScheduleLobbyAvatarRefresh($"scene unloaded: {scene.name}");
        }
    }

    private void OnActiveSceneChanged(Scene oldScene, Scene newScene)
    {
        if (string.Equals(newScene.name, "GameCore", System.StringComparison.Ordinal))
        {
            MpCustomAvatarLobbyTransferManager.SuspendLobbyAvatarFileTransfer();
            ScheduleArenaAvatarScan();
        }

        if (string.Equals(oldScene.name, "GameCore", System.StringComparison.Ordinal))
            ScheduleLobbyAvatarRefresh($"left GameCore -> {newScene.name}");
    }

    private Coroutine? _pendingArenaScan;

    private static readonly float[] ArenaScanDelaySeconds = { 0.35f, 1f, 2f, 4f, 7f, 11f, 16f };

    public static void ScheduleArenaAvatarScan()
    {
        if (!MpChatFeatures.LobbyCustomAvatars || !ModSettings.EnableLobbyCustomAvatars)
            return;
        if (!MpChatFeatures.LobbyCustomAvatarsInArena)
            return;

        Instance?.ScheduleArenaAvatarScanInternal();
    }

    private void ScheduleArenaAvatarScanInternal()
    {
        if (_pendingArenaScan != null)
            StopCoroutine(_pendingArenaScan);
        _pendingArenaScan = StartCoroutine(ScanArenaAvatarsAfterGameCoreLoad());
    }

    private IEnumerator ScanArenaAvatarsAfterGameCoreLoad()
    {
        var previous = 0f;
        for (var i = 0; i < ArenaScanDelaySeconds.Length; i++)
        {
            var delay = ArenaScanDelaySeconds[i];
            yield return new WaitForSecondsRealtime(delay - previous);
            previous = delay;

            if (!MpChatLobbyDiagnostics.AnyGameCoreLoaded())
                break;

            MpChatArenaAvatarAttach.ScanGameCoreAvatars();
            MpChatLobbyCustomAvatarDriverRegistry.WakePendingArenaLoads();
        }

        _pendingArenaScan = null;
    }

    public static void ScheduleLobbySessionRejoinRefresh()
    {
        if (!MpChatFeatures.LobbyCustomAvatars || !ModSettings.EnableLobbyCustomAvatars)
            return;

        Instance?.ScheduleLobbyAvatarRefresh("lobby session re-entry");
    }

    private void ScheduleLobbyAvatarRefresh(string reason)
    {
        if (!MpChatFeatures.LobbyCustomAvatars || !ModSettings.EnableLobbyCustomAvatars)
            return;

        if (_pendingRefresh != null)
            StopCoroutine(_pendingRefresh);
        _pendingRefresh = StartCoroutine(RefreshAfterLobbyReturn(reason));
    }

    private IEnumerator RefreshAfterLobbyReturn(string reason)
    {
        _lobbyReturnRefreshInProgress = true;
        try
        {
            MpChatLobbyDiagnostics.InvalidateSceneHeuristicCaches();
            MpChatAddonPacketBridge.ReattachCallbacks();
            MpChatLobbyPosePoll.ClearAll();

            const float lobbyWaitTimeoutSeconds = 6f;
            var waitStart = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - waitStart < lobbyWaitTimeoutSeconds)
            {
                yield return null;
                if (MpChatLobbyDiagnostics.LobbyHierarchyLooksLikeMultiplayerLobby())
                    break;
                yield return new WaitForSecondsRealtime(0.15f);
            }

            yield return new WaitForSecondsRealtime(1.5f);

            if (!MpChatFeatures.LobbyCustomAvatars || !ModSettings.EnableLobbyCustomAvatars)
                yield break;

            if (!MpChatLobbyDiagnostics.LobbyHierarchyLooksLikeMultiplayerLobby())
                yield break;

            MpCustomAvatarSyncManager.EnsureActiveLobbyHostAfterArena();
            MpCustomAvatarLobbyTransferManager.ScheduleGradualLobbyReturnFlush();
            MpCustomAvatarSyncManager.PollDeferredAvatarUpdates();
            yield return MpChatLobbyCustomAvatarDriver.RefreshAllLobbyPedestalsStaggered(forceRespawn: false);

            RebootstrapConnectedLobbyAvatars();
            yield return FollowUpLobbyAvatarRefreshIfNeeded();
            MpChatLobbyCustomAvatarDriver.ReregisterAllPosePolls();

            MultiplayerChat.Plugin.Log?.Debug($"[MPChat][LobbyAvatar] Refreshed lobby avatars after {reason}");
        }
        finally
        {
            _lobbyReturnRefreshInProgress = false;
            _pendingRefresh = null;
        }
    }

    private static void RebootstrapConnectedLobbyAvatars()
    {
        MpCustomAvatarSyncManager.EnsureActiveLobbyHostAfterArena();
        var session = UnityEngine.Object.FindObjectOfType<MultiplayerSessionManager>();
        if (session?.connectedPlayers == null)
            return;

        var local = session.localPlayer;
        for (var i = 0; i < session.connectedPlayers.Count; i++)
        {
            var player = session.connectedPlayers[i];
            if (player == null || string.IsNullOrEmpty(player.userId))
                continue;
            if (local != null && player.userId == local.userId)
                continue;

            MpCustomAvatarSyncManager.ScheduleJoinRetry(player.userId);
            QueuePlayerJoinAvatarWork(player.userId);
        }
    }

    private static IEnumerator FollowUpLobbyAvatarRefreshIfNeeded()
    {
        yield return new WaitForSecondsRealtime(2f);

        if (!MpChatFeatures.LobbyCustomAvatars || !ModSettings.EnableLobbyCustomAvatars)
            yield break;

        if (!MpChatLobbyDiagnostics.LobbyHierarchyLooksLikeMultiplayerLobby())
            yield break;

        var session = UnityEngine.Object.FindObjectOfType<MultiplayerSessionManager>();
        if (session?.connectedPlayers == null)
            yield break;

        var local = session.localPlayer;
        var anyFollowUp = false;
        for (var i = 0; i < session.connectedPlayers.Count; i++)
        {
            var player = session.connectedPlayers[i];
            if (player == null || string.IsNullOrEmpty(player.userId))
                continue;
            if (local != null && player.userId == local.userId)
                continue;

            if (!MpCustomAvatarSyncManager.TryGetRemoteState(player.userId, out var row))
                continue;

            var descriptorHash = row.AvatarDescriptorId;
            if (string.IsNullOrEmpty(descriptorHash) ||
                CustomAvatarInstallListing.IsVanillaDescriptorHash(descriptorHash))
                continue;

            if (!MpChatLobbyCustomAvatarDriver.AnyPedestalNeedsSpawn(player.userId, descriptorHash!))
                continue;

            anyFollowUp = true;
            MpCustomAvatarSyncManager.ScheduleJoinRetry(player.userId);
            MpChatLobbyCustomAvatarDriver.ProcessPlayerJoinedImmediate(player.userId);
            yield return null;
        }

        if (anyFollowUp)
            MpChatLobbyCustomAvatarDriverRegistry.WakePendingLobbyLoads();
    }
}
