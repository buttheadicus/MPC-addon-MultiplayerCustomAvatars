using System;
using System.Collections;
using System.Collections.Generic;
using MultiplayerChat.Network;
using MultiplayerChat.Settings;
using MultiplayerCore.Models;
using MultiplayerCore.Networking;
using UnityEngine;
using Zenject;

namespace MultiplayerChat.Core;

public sealed class MpCustomAvatarSyncManager : MonoBehaviour, IInitializable
{
    public static MpCustomAvatarSyncManager? Instance { get; private set; }

    private static MpCustomAvatarSyncManager? _lobbyScopeSyncManager;

    public static event Action<string>? RemoteLobbyAvatarUpdated;

    private static readonly object RemoteLock = new();

    private static readonly Dictionary<string, MpCustomAvatarRemoteState> RemoteByUserId =
        new(StringComparer.Ordinal);

    private static readonly object DeferredNotifyLock = new();

    private static readonly HashSet<string> DeferredNotifyUserIds = new(StringComparer.Ordinal);

    private static readonly object JoinRetryLock = new();

    private static readonly Dictionary<string, float> JoinRetryDeadlineByUserId =
        new(StringComparer.Ordinal);

    private const float JoinRetryWindowSeconds = 30f;

    private const float JoinRetryPollIntervalSeconds = 0.75f;

    private const float MaintenanceIntervalSeconds = 0.35f;

    private static float _lastJoinRetryPollRealtime;

    private const float ScaleEpsilon = 0.002f;

    [Inject(Optional = true)] private readonly IMultiplayerSessionManager? _sessionManager;

    private Coroutine? _maintenanceRoutine;

    private static readonly WaitForSeconds MaintenanceWait =
        new WaitForSeconds(MaintenanceIntervalSeconds);

    private readonly MpCustomAvatarPosePacket _outboundPacket = new();

    private bool _started;

    private bool _lobbySessionBootstrapComplete;

    private Coroutine? _lobbyBootstrapRoutine;

    public void Initialize() => EnsureInitialized();

    // Persistent addon hosts are not Zenject-initialized; CustomAvatarsAddon calls this after Inject.
    public void EnsureInitialized()
    {
        if (_started)
            return;

        _started = true;
        var gameCoreHost = MpChatSceneScope.IsGameCoreHost(this);

        if (gameCoreHost)
        {
            Instance = this;
            MultiplayerChat.Plugin.Log?.Debug("[MPChat][LobbyAvatar] Sync manager active (GameCore host)");
            StartMaintenanceLoop();
            StartCoroutine(BootstrapArenaRemoteAvatars());
            return;
        }

        _lobbyScopeSyncManager = this;
        if (Instance == null || !MpChatSceneScope.IsGameCoreHost(Instance))
        {
            Instance = this;
            MultiplayerChat.Plugin.Log?.Debug("[MPChat][LobbyAvatar] Sync manager active (lobby host)");
        }

        ResetSessionAvatarSyncState();
        StartMaintenanceLoop();
        if (ModSettings.EnableLobbyCustomAvatars && ModSettings.HasLobbyCustomAvatarSavedEyeHeight)
            StartCoroutine(ApplySavedEyeHeightWhenReady());
    }

    private IMultiplayerSessionManager? ResolveSessionManager() =>
        MpChatAddonSessionResolver.Resolve(_sessionManager);

    // Remote metadata is static across lobby + GameCore; hand Instance back after arena teardown.
    internal static void EnsureActiveLobbyHostAfterArena()
    {
        if (!MpChatLobbyDiagnostics.MultiplayerLobbyReturnContextActive())
            return;
        if (MpChatLobbyDiagnostics.BeatmapGameplayLikelyActive())
            return;

        var lobby = _lobbyScopeSyncManager;
        if (lobby == null)
            return;

        if (Instance == null || MpChatSceneScope.IsGameCoreHost(Instance))
            Instance = lobby;
    }

    internal static void OnVoipPipelineReloaded()
    {
        MpChatAddonPacketBridge.ReattachCallbacks();
        EnsureActiveLobbyHostAfterArena();
        if (Instance != null && MpChatSceneScope.IsGameCoreHost(Instance)
            && !MpChatLobbyDiagnostics.AnyGameCoreLoaded() && _lobbyScopeSyncManager != null)
            Instance = _lobbyScopeSyncManager;

        if (MpChatLobbyDiagnostics.AnyGameCoreLoaded())
            MpChatLobbyAvatarLifecycleHost.ScheduleArenaAvatarScan();
    }

    private static MpCustomAvatarSyncManager? ResolveActiveSendHost()
    {
        var lobby = _lobbyScopeSyncManager;
        if (lobby != null && lobby.isActiveAndEnabled)
            return lobby;

        if (Instance != null && !MpChatSceneScope.IsGameCoreHost(Instance))
            return Instance;

        return lobby;
    }

    private IEnumerator BootstrapArenaRemoteAvatars()
    {
        if (!MpChatFeatures.LobbyCustomAvatars || !MpChatFeatures.LobbyCustomAvatarsInArena)
            yield break;
        if (!ModSettings.EnableLobbyCustomAvatars)
            yield break;

        yield return new WaitForSecondsRealtime(0.35f);
        MpChatArenaAvatarAttach.ScanGameCoreAvatars();

        var session = ResolveSessionManager();
        var connected = session?.connectedPlayers;
        var local = session?.localPlayer;
        if (connected == null)
            yield break;

        for (var i = 0; i < connected.Count; i++)
        {
            var player = connected[i];
            if (player == null || string.IsNullOrEmpty(player.userId))
                continue;
            if (local != null && player.userId == local.userId)
                continue;

            NotifyRemoteAvatarMayBeReady(player.userId);
            yield return null;
        }

        yield return new WaitForSecondsRealtime(0.5f);
        MpChatArenaAvatarAttach.ScanGameCoreAvatars();
    }

    private IEnumerator BootstrapExistingRemoteAvatars()
    {
        try
        {
            if (MpChatSceneScope.IsGameCoreHost(this))
                yield break;

            const float waitTimeoutSeconds = 8f;
            var waitStart = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - waitStart < waitTimeoutSeconds)
            {
                if (MpChatLobbyDiagnostics.LobbyHierarchyLooksLikeMultiplayerLobby())
                    break;

                yield return new WaitForSeconds(0.25f);
            }

            if (!MpChatFeatures.LobbyCustomAvatars || !ModSettings.EnableLobbyCustomAvatars)
                yield break;

            var session = ResolveSessionManager();
            if (!MpChatLobbyDiagnostics.MultiplayerAvatarSyncContextActive(session))
                yield break;

            if (MpChatPerformanceGate.IsMultiplayerSceneTransitionLikely())
                yield break;

            var local = session?.localPlayer;
            var connected = session?.connectedPlayers;
            if (connected == null)
                yield break;

            for (var i = 0; i < connected.Count; i++)
            {
                var player = connected[i];
                if (player == null || string.IsNullOrEmpty(player.userId))
                    continue;
                if (local != null && player.userId == local.userId)
                    continue;

                NotifyRemoteAvatarMayBeReady(player.userId);
                yield return null;
            }

            BroadcastLocalMetadataToSession();
            _lobbySessionBootstrapComplete = true;
        }
        finally
        {
            _lobbyBootstrapRoutine = null;
        }
    }

    private void TryStartLobbySessionBootstrap()
    {
        if (_lobbySessionBootstrapComplete || _lobbyBootstrapRoutine != null)
            return;
        if (!MpChatFeatures.LobbyCustomAvatars || !ModSettings.EnableLobbyCustomAvatars)
            return;

        MpChatAddonPacketBridge.ReattachCallbacks();

        var session = ResolveSessionManager();
        if (session?.localPlayer == null || string.IsNullOrEmpty(session.localPlayer.userId))
            return;
        if (!MpChatLobbyDiagnostics.MultiplayerAvatarSyncContextActive(session))
            return;

        _lobbyBootstrapRoutine = StartCoroutine(BootstrapExistingRemoteAvatars());
    }

    private IEnumerator ApplySavedEyeHeightWhenReady()
    {
        const int maxAttempts = 24;
        for (var i = 0; i < maxAttempts; i++)
        {
            if (!ModSettings.EnableLobbyCustomAvatars)
                yield break;

            if (MpCustomAvatarHeightCalibration.ApplySavedPresetIfAny())
                yield break;

            yield return new WaitForSeconds(0.5f);
        }
    }

    public static bool TryGetRemoteState(string userId, out MpCustomAvatarRemoteState state)
    {
        state = null!;
        if (string.IsNullOrEmpty(userId))
            return false;
        lock (RemoteLock)
            return RemoteByUserId.TryGetValue(userId, out state!);
    }

    public static void ApplyReceived(string userId, MpCustomAvatarPosePacket packet)
    {
        if (!MpChatFeatures.LobbyCustomAvatars)
            return;
        if (string.IsNullOrEmpty(userId))
            return;

        var hasDescriptor = (packet.Flags & MpCustomAvatarPosePacket.FlagHasDescriptor) != 0;
        var hasScale = (packet.Flags & MpCustomAvatarPosePacket.FlagHasScale) != 0;
        var descriptor = hasDescriptor ? packet.AvatarDescriptorId : null;
        var scale = hasScale ? Mathf.Clamp(packet.AvatarScale, 0.25f, 4f) : 1f;
        var changed = false;
        var descriptorChanged = false;
        var newRemoteRow = false;

        lock (RemoteLock)
        {
            if (!RemoteByUserId.TryGetValue(userId, out var row))
            {
                row = new MpCustomAvatarRemoteState();
                RemoteByUserId[userId] = row;
                newRemoteRow = true;
                changed = true;
            }

            if (hasDescriptor &&
                !string.Equals(row.AvatarDescriptorId, descriptor, StringComparison.OrdinalIgnoreCase))
            {
                row.AvatarDescriptorId = descriptor?.Trim().ToUpperInvariant();
                descriptorChanged = true;
                changed = true;
            }

            if (hasScale && Mathf.Abs(row.AvatarScale - scale) > ScaleEpsilon)
            {
                row.AvatarScale = scale;
                changed = true;
                if (!hasDescriptor)
                    row.PendingScaleRespawn = true;
            }

            if (changed)
                row.ReceivedAtRealtime = Time.realtimeSinceStartup;
        }

        if (!changed && hasDescriptor && !string.IsNullOrEmpty(descriptor))
        {
            var hashProbe = descriptor!.Trim().ToUpperInvariant();
            if (MpChatLobbyCustomAvatarDriver.AnyPedestalNeedsSpawn(userId, hashProbe))
                changed = true;
        }

        if (changed)
        {
            if (descriptorChanged && !string.IsNullOrEmpty(descriptor))
            {
                MultiplayerChat.Plugin.Log?.Info(
                    $"[MPChat][LobbyAvatar] Remote avatar metadata from {userId} hash={descriptor!.Trim().ToUpperInvariant()}");
            }

            if (MpChatLobbyDiagnostics.AnyGameCoreLoaded())
            {
                if (MpChatLobbyDiagnostics.ResultsLikeUiVisible())
                    MpChatResultsPedestalAttach.ScanResultsPedestals(force: true);
                else
                    MpChatLobbyCustomAvatarDriverRegistry.ForUser(
                        userId,
                        driver => driver.KickFromRemoteSync(),
                        lobbyPedestalsOnly: false);
            }
            else if (MpChatAvatarWorkloadGate.ShouldDeferAvatarNetworkDiskAndSpawnWork ||
                     MpChatPerformanceGate.ShouldDeferIncomingAvatarData)
            {
                lock (DeferredNotifyLock)
                    DeferredNotifyUserIds.Add(userId);
            }
            else
            {
                RemoteLobbyAvatarUpdated?.Invoke(userId);
            }

            ClearJoinRetry(userId);

            if (descriptorChanged || newRemoteRow)
            {
                var hash = descriptor?.Trim().ToUpperInvariant() ?? "";
                if (CustomAvatarHashUtil.LooksLikeMd5Hex(hash) &&
                    !CustomAvatarInstallListing.IsVanillaDescriptorHash(hash) &&
                    !CustomAvatarLobbyHashCache.TryGetPath(hash, out _))
                    MpCustomAvatarLobbyTransferManager.RequestLobbyAvatarFile(hash, userId);
            }
        }
    }

    public static void PollDeferredAvatarUpdates()
    {
        var deferring = MpChatAvatarWorkloadGate.ShouldDeferAvatarNetworkDiskAndSpawnWork ||
                        MpChatPerformanceGate.ShouldDeferIncomingAvatarData;
        if (deferring)
            return;

        lock (DeferredNotifyLock)
        {
            if (DeferredNotifyUserIds.Count == 0)
                return;

            using var enumerator = DeferredNotifyUserIds.GetEnumerator();
            enumerator.MoveNext();
            var userId = enumerator.Current;
            DeferredNotifyUserIds.Remove(userId);
            RemoteLobbyAvatarUpdated?.Invoke(userId);
        }
    }

    public static void NotifyAllRemotesWithHash(string hash)
    {
        if (string.IsNullOrEmpty(hash))
            return;

        hash = hash.Trim().ToUpperInvariant();
        lock (RemoteLock)
        {
            if (RemoteByUserId.Count == 0)
                return;

            lock (DeferredNotifyLock)
            {
                foreach (var kvp in RemoteByUserId)
                {
                    if (!string.Equals(kvp.Value.AvatarDescriptorId, hash, StringComparison.OrdinalIgnoreCase))
                        continue;

                    DeferredNotifyUserIds.Add(kvp.Key);
                }
            }
        }
    }

    public static void NotifyRemoteAvatarMayBeReady(string userId, bool sendMetadataToJoiner = false)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        ScheduleJoinRetry(userId);
        MpChatLobbyAvatarLifecycleHost.QueuePlayerJoinAvatarWork(userId, sendMetadataToJoiner);

        if (MpChatAvatarWorkloadGate.ShouldDeferAvatarNetworkDiskAndSpawnWork ||
            MpChatPerformanceGate.ShouldDeferIncomingAvatarData)
        {
            lock (DeferredNotifyLock)
                DeferredNotifyUserIds.Add(userId);
            return;
        }

        if (TryGetRemoteState(userId, out var row) &&
            !string.IsNullOrEmpty(row.AvatarDescriptorId) &&
            !CustomAvatarInstallListing.IsVanillaDescriptorHash(row.AvatarDescriptorId))
        {
            RemoteLobbyAvatarUpdated?.Invoke(userId);
        }
    }

    public static void ScheduleJoinRetry(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        var deadline = Time.realtimeSinceStartup + JoinRetryWindowSeconds;
        lock (JoinRetryLock)
            JoinRetryDeadlineByUserId[userId] = deadline;
    }

    private static void ClearJoinRetry(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        lock (JoinRetryLock)
            JoinRetryDeadlineByUserId.Remove(userId);
    }

    private static void PollJoinRetries()
    {
        if (MpChatAvatarWorkloadGate.ShouldDeferAvatarNetworkDiskAndSpawnWork ||
            MpChatPerformanceGate.ShouldDeferIncomingAvatarData)
            return;

        string[] pending;
        lock (JoinRetryLock)
        {
            if (JoinRetryDeadlineByUserId.Count == 0)
                return;

            pending = new string[JoinRetryDeadlineByUserId.Count];
            JoinRetryDeadlineByUserId.Keys.CopyTo(pending, 0);
        }

        var now = Time.realtimeSinceStartup;
        if (now - _lastJoinRetryPollRealtime < JoinRetryPollIntervalSeconds)
            return;

        _lastJoinRetryPollRealtime = now;

        foreach (var userId in pending)
        {
            float deadline;
            lock (JoinRetryLock)
            {
                if (!JoinRetryDeadlineByUserId.TryGetValue(userId, out deadline))
                    continue;
            }

            if (now > deadline)
            {
                ClearJoinRetry(userId);
                continue;
            }

            if (MpChatLobbyCustomAvatarDriver.TryCompleteJoinRefresh(userId))
                ClearJoinRetry(userId);
        }
    }

    public static void ClearRemote(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return;
        lock (RemoteLock)
            RemoteByUserId.Remove(userId);

        ClearJoinRetry(userId);
        MpChatLobbyAvatarLifecycleHost.QueuePlayerLeaveAvatarWork(userId);
    }

    // Static remote rows survive lobby Zenject teardown; clear when the lobby session ends.
    public static void ClearAllRemotes()
    {
        lock (RemoteLock)
            RemoteByUserId.Clear();

        lock (JoinRetryLock)
            JoinRetryDeadlineByUserId.Clear();

        lock (DeferredNotifyLock)
            DeferredNotifyUserIds.Clear();

        MpCustomAvatarLobbyTransferManager.ClearLobbyAvatarTransferMemoryCaches();
    }

    public static void ResetSessionAvatarSyncState()
    {
        ClearAllRemotes();
        if (Instance != null)
        {
            Instance._lobbySessionBootstrapComplete = false;
            Instance._lobbyBootstrapRoutine = null;
        }
    }

    // Full teardown when the multiplayer session is no longer connected.
    public static void FlushLobbyCustomAvatarsOnServerLeaveIfDisconnected()
    {
        if (!MpChatFeatures.LobbyCustomAvatars)
            return;

        try
        {
            if (MpMultiplayerSessionReflection.IsSessionConnected())
                return;
        }
        catch
        {
            /* fall through to flush */
        }

        FlushLobbyCustomAvatarsOnServerLeave();
    }

    public static void FlushLobbyCustomAvatarsOnServerLeave()
    {
        if (!MpChatFeatures.LobbyCustomAvatars)
            return;

        MpCustomAvatarLobbyTransferManager.SuspendLobbyAvatarFileTransfer(discardInFlightSendQueue: true);
        MpChatLobbyAvatarLifecycleHost.CancelPendingAvatarWork();
        MpChatLobbyCustomAvatarDriver.FlushAllOnServerLeave();
        MpChatLobbyPosePoll.ClearAll();
        MpChatArenaAvatarAttach.DestroyOrphanedArenaObjects();
        CustomAvatarLobbyHashCache.Invalidate();
        StopLobbyScopeMaintenanceIfActive();
        ResetSessionAvatarSyncState();
        MpCustomAvatarScaleSource.InvalidateCachedManager();
    }

    private static void StopLobbyScopeMaintenanceIfActive()
    {
        var lobby = _lobbyScopeSyncManager;
        if (lobby == null || lobby._maintenanceRoutine == null)
            return;

        lobby.StopCoroutine(lobby._maintenanceRoutine);
        lobby._maintenanceRoutine = null;
    }

    private static float _lastMissingRemoteScanRealtime = -999f;

    private const float MissingRemoteScanCooldownSeconds = 1.25f;

    public static void RequestMissingRemoteAvatarFiles()
    {
        if (!MpChatFeatures.LobbyCustomAvatars || !ModSettings.EnableLobbyCustomAvatars)
            return;
        if (!MpChatPerformanceGate.CanRunLobbyAvatarFileTransfer)
            return;

        var now = Time.realtimeSinceStartup;
        if (now - _lastMissingRemoteScanRealtime < MissingRemoteScanCooldownSeconds)
            return;

        _lastMissingRemoteScanRealtime = now;

        string[] userIds;
        lock (RemoteLock)
        {
            if (RemoteByUserId.Count == 0)
                return;

            userIds = new string[RemoteByUserId.Count];
            RemoteByUserId.Keys.CopyTo(userIds, 0);
        }

        for (var i = 0; i < userIds.Length; i++)
        {
            var userId = userIds[i];
            if (string.IsNullOrEmpty(userId))
                continue;

            if (!TryGetRemoteState(userId, out var row))
                continue;

            var hash = row.AvatarDescriptorId?.Trim().ToUpperInvariant() ?? "";
            if (!CustomAvatarHashUtil.LooksLikeMd5Hex(hash))
                continue;
            if (CustomAvatarInstallListing.IsVanillaDescriptorHash(hash))
                continue;
            if (CustomAvatarLobbyHashCache.TryGetPath(hash, out _))
                continue;

            MpCustomAvatarLobbyTransferManager.RequestLobbyAvatarFile(hash, userId);
        }
    }

    public static void InvalidateOutboundDedupe()
    {
        // Kept for settings callers; metadata is event-driven now (no outbound dedupe state).
    }

    internal static bool ShouldAcceptPosePacket(MpCustomAvatarPosePacket packet)
    {
        if (string.IsNullOrEmpty(packet.TargetUserId))
            return true;

        var local = Instance?.ResolveSessionManager()?.localPlayer;
        return local != null &&
               !string.IsNullOrEmpty(local.userId) &&
               string.Equals(packet.TargetUserId, local.userId, StringComparison.Ordinal);
    }

    // Tell everyone in the session which avatar we use (session join / avatar change / height calibrate).
    public static void BroadcastLocalMetadataToAll()
    {
        ResolveActiveSendHost()?.SendLocalMetadata(targetUserId: null, includeDescriptor: true, includeScale: true);
    }

    // Tell existing peers our avatar once after we join their session.
    public static void BroadcastLocalMetadataToSession() => BroadcastLocalMetadataToAll();

    // Tell one newly joined peer our avatar; they request the file if needed.
    public static void SendLocalMetadataToUser(string targetUserId)
    {
        if (string.IsNullOrEmpty(targetUserId))
            return;

        ResolveActiveSendHost()?.SendLocalMetadata(targetUserId, includeDescriptor: true, includeScale: true);
    }

    // Height calibrate: scale packet first so peers apply height before a full metadata/spawn refresh.
    public static void BroadcastScaleThenMetadata()
    {
        var host = ResolveActiveSendHost();
        host?.SendLocalMetadata(targetUserId: null, includeDescriptor: false, includeScale: true);
        host?.SendLocalMetadata(targetUserId: null, includeDescriptor: true, includeScale: true);
    }

    public static void RunHeightCalibration()
    {
        if (Instance != null)
            Instance.StartCoroutine(HeightCalibrationCoroutine());
        else if (MpChatLobbyAvatarLifecycleHost.Instance != null)
            MpChatLobbyAvatarLifecycleHost.Instance.StartCoroutine(HeightCalibrationCoroutine());
        else if (MpCustomAvatarHeightCalibration.TryRunCalibration())
            MpCustomAvatarHeightCalibration.RefreshLocalLobbyAvatar();
    }

    private void OnDestroy()
    {
        var gameCoreHost = MpChatSceneScope.IsGameCoreHost(this);
        var lobbyPeer = _lobbyScopeSyncManager;
        var iAmLobby = ReferenceEquals(_lobbyScopeSyncManager, this);

        if (iAmLobby)
            _lobbyScopeSyncManager = null;

        if (_maintenanceRoutine != null)
        {
            StopCoroutine(_maintenanceRoutine);
            _maintenanceRoutine = null;
        }

        if (ReferenceEquals(Instance, this))
        {
            if (gameCoreHost && lobbyPeer != null && !ReferenceEquals(lobbyPeer, this))
                Instance = lobbyPeer;
            else
                Instance = null;
        }
    }

    private static IEnumerator HeightCalibrationCoroutine()
    {
        yield return null;
        yield return MpCustomAvatarHeightCalibration.EnsurePamAvatarReadyCoroutine();
        yield return null;

        if (!MpCustomAvatarHeightCalibration.TryRunCalibration())
            yield break;

        yield return null;
        MpCustomAvatarHeightCalibration.RefreshLocalLobbyAvatar();
    }

    public static bool TryConsumePendingScaleRespawn(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return false;

        lock (RemoteLock)
        {
            if (!RemoteByUserId.TryGetValue(userId, out var row))
                return false;
            if (!row.PendingScaleRespawn)
                return false;
            row.PendingScaleRespawn = false;
            return true;
        }
    }

    private void StartMaintenanceLoop()
    {
        if (_maintenanceRoutine != null)
            StopCoroutine(_maintenanceRoutine);
        _maintenanceRoutine = StartCoroutine(MaintenanceLoop());
    }

    private IEnumerator MaintenanceLoop()
    {
        while (true)
        {
            yield return MaintenanceWait;
            var session = ResolveSessionManager();
            if (!MpChatLobbyDiagnostics.MultiplayerAvatarSyncContextActive(session))
                continue;

            TryStartLobbySessionBootstrap();

            if (MpChatLobbyDiagnostics.AnyGameCoreLoaded())
            {
                if (MpChatLobbyDiagnostics.ResultsLikeUiVisible())
                    MpChatResultsPedestalAttach.ScanResultsPedestals();
                else
                    MpChatLobbyCustomAvatarDriverRegistry.WakePendingArenaLoads();
            }
            else if (!MpChatPerformanceGate.ShouldBlockAvatarHeavyWork &&
                     MpChatLobbyDiagnostics.LobbyHierarchyLooksLikeMultiplayerLobby())
            {
                PollDeferredAvatarUpdates();
                PollJoinRetries();
                MpCustomAvatarLobbyTransferManager.PollDeferredCacheWrites(maxWrites: 1);
                MpCustomAvatarLobbyTransferManager.PollDeferredOutbound(maxJobs: 1);
            }
        }
    }

    private void SendLocalMetadata(string? targetUserId, bool includeDescriptor, bool includeScale)
    {
        if (!MpChatFeatures.LobbyCustomAvatars)
            return;
        if (!ModSettings.EnableLobbyCustomAvatars)
            return;
        if (!ReferenceEquals(ResolveActiveSendHost(), this))
            return;

        var session = ResolveSessionManager();
        var local = session?.localPlayer;
        if (local == null || string.IsNullOrEmpty(local.userId))
        {
            MultiplayerChat.Plugin.Log?.Warn("[MPChat][LobbyAvatar] Metadata send skipped: no local player in session");
            return;
        }

        string? descriptor = null;
        if (includeDescriptor)
        {
            descriptor = ModSettings.LobbyCustomAvatarContentHash.Trim().ToUpperInvariant();
            if (!CustomAvatarHashUtil.LooksLikeMd5Hex(descriptor))
            {
                MultiplayerChat.Plugin.Log?.Warn("[MPChat][LobbyAvatar] Metadata send skipped: invalid avatar hash");
                return;
            }
            if (descriptor.Length > MpCustomAvatarPosePacket.MaxDescriptorChars)
                descriptor = descriptor.Substring(0, MpCustomAvatarPosePacket.MaxDescriptorChars);
        }

        var scale = 1f;
        if (includeScale && !MpCustomAvatarScaleSource.TryGetLocalAvatarScale(out scale))
            scale = 1f;

        byte flags = 0;
        if (includeDescriptor && !string.IsNullOrEmpty(descriptor))
            flags |= MpCustomAvatarPosePacket.FlagHasDescriptor;
        if (includeScale)
            flags |= MpCustomAvatarPosePacket.FlagHasScale;

        if (flags == 0)
            return;

        _outboundPacket.Flags = flags;
        _outboundPacket.AvatarDescriptorId = includeDescriptor ? descriptor : null;
        _outboundPacket.AvatarScale = scale;
        _outboundPacket.FbtBlob = null;
        _outboundPacket.TargetUserId = string.IsNullOrEmpty(targetUserId) ? null : targetUserId;

        try
        {
            session?.Send(_outboundPacket);
            MultiplayerChat.Plugin.Log?.Info(
                $"[MPChat][LobbyAvatar] Sent avatar metadata to {(string.IsNullOrEmpty(targetUserId) ? "session" : targetUserId)} hash={descriptor ?? "(scale only)"}");
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Send failed: {ex.Message}");
        }
        finally
        {
            _outboundPacket.TargetUserId = null;
        }
    }
}

public sealed class MpCustomAvatarRemoteState
{
    public string? AvatarDescriptorId;

    public float AvatarScale = 1f;

    public float ReceivedAtRealtime;

    public bool PendingScaleRespawn;
}
