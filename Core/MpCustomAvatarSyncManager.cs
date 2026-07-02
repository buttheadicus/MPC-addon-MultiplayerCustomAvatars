using System;
using System.Collections;
using System.Collections.Generic;
using MultiplayerChat.Core.QuickBinds;
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

    private static bool _wasDeferringIncomingAvatarData;

    private static readonly object JoinRetryLock = new();

    private static readonly Dictionary<string, float> JoinRetryDeadlineByUserId =
        new(StringComparer.Ordinal);

    private const float MetadataBroadcastIntervalSeconds = 2f;

    private const float JoinRetryWindowSeconds = 30f;

    private const float JoinRetryPollIntervalSeconds = 0.75f;

    private const float ForcedMetadataMinIntervalSeconds = 2.5f;

    private static float _lastForcedMetadataRealtime = -999f;

    private static float _lastJoinRetryPollRealtime;

    private const float MetadataKeepaliveSeconds = 25f;

    private const float ScaleEpsilon = 0.002f;

    [Inject] private readonly IMultiplayerSessionManager _sessionManager = null!;

    private Coroutine? _broadcastRoutine;

    private static readonly WaitForSeconds MetadataWait =
        new WaitForSeconds(MetadataBroadcastIntervalSeconds);

    private string? _lastSentDescriptor;

    private float _lastSentScale = 1f;

    private float _lastSendRealtime;

    private readonly MpCustomAvatarPosePacket _outboundPacket = new();

    public void Initialize()
    {
        var gameCoreHost = MpChatSceneScope.IsGameCoreHost(this);

        if (gameCoreHost)
        {
            Instance = this;
            ClearBroadcastDedupeState();
            MultiplayerChat.Plugin.Log?.Debug("[MPChat][LobbyAvatar] Sync manager active (GameCore host)");
            StartBroadcastLoop();
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
        StartBroadcastLoop();
        StartCoroutine(BootstrapExistingRemoteAvatars());
        if (ModSettings.EnableLobbyCustomAvatars && ModSettings.HasLobbyCustomAvatarSavedEyeHeight)
            StartCoroutine(ApplySavedEyeHeightWhenReady());
    }

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
        if (Instance != null && MpChatSceneScope.IsGameCoreHost(Instance)
            && !MpChatLobbyDiagnostics.AnyGameCoreLoaded() && _lobbyScopeSyncManager != null)
            Instance = _lobbyScopeSyncManager;
    }

    private IEnumerator BootstrapArenaRemoteAvatars()
    {
        if (!MpChatFeatures.LobbyCustomAvatars || !MpChatFeatures.LobbyCustomAvatarsInArena)
            yield break;
        if (!ModSettings.EnableLobbyCustomAvatars)
            yield break;

        yield return new WaitForSecondsRealtime(0.35f);
        MpChatArenaAvatarAttach.ScanGameCoreAvatars();

        var connected = _sessionManager.connectedPlayers;
        var local = _sessionManager.localPlayer;
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

        if (!MpChatLobbyDiagnostics.MultiplayerAvatarSyncContextActive(_sessionManager))
            yield break;

        if (MpChatPerformanceGate.IsMultiplayerSceneTransitionLikely())
            yield break;

        var local = _sessionManager.localPlayer;
        var connected = _sessionManager.connectedPlayers;
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

        BroadcastMetadataNow(applySavedEyeHeight: false, forceSend: true);
    }

    private IEnumerator ApplySavedEyeHeightWhenReady()
    {
        const int maxAttempts = 24;
        for (var i = 0; i < maxAttempts; i++)
        {
            if (!ModSettings.EnableLobbyCustomAvatars)
                yield break;

            if (MpCustomAvatarHeightCalibration.ApplySavedPresetIfAny())
            {
                InvalidateOutboundDedupe();
                TryBroadcastMetadata();
                yield break;
            }

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
            if (MpChatPerformanceGate.ShouldDeferIncomingAvatarData)
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
        var deferring = MpChatPerformanceGate.ShouldDeferIncomingAvatarData;
        if (deferring)
        {
            _wasDeferringIncomingAvatarData = true;
            return;
        }

        if (!_wasDeferringIncomingAvatarData && DeferredNotifyUserIds.Count == 0)
            return;

        _wasDeferringIncomingAvatarData = false;
        string[] pending;
        lock (DeferredNotifyLock)
        {
            if (DeferredNotifyUserIds.Count == 0)
                return;
            pending = new string[DeferredNotifyUserIds.Count];
            DeferredNotifyUserIds.CopyTo(pending);
            DeferredNotifyUserIds.Clear();
        }

        foreach (var userId in pending)
            RemoteLobbyAvatarUpdated?.Invoke(userId);
    }

    public static void NotifyAllRemotesWithHash(string hash)
    {
        if (string.IsNullOrEmpty(hash))
            return;

        hash = hash.Trim().ToUpperInvariant();
        string[] userIds;
        lock (RemoteLock)
        {
            if (RemoteByUserId.Count == 0)
                return;

            var matches = new List<string>(RemoteByUserId.Count);
            foreach (var kvp in RemoteByUserId)
            {
                if (string.Equals(kvp.Value.AvatarDescriptorId, hash, StringComparison.OrdinalIgnoreCase))
                    matches.Add(kvp.Key);
            }

            if (matches.Count == 0)
                return;

            userIds = matches.ToArray();
        }

        for (var i = 0; i < userIds.Length; i++)
            RemoteLobbyAvatarUpdated?.Invoke(userIds[i]);
    }

    public static void NotifyRemoteAvatarMayBeReady(string userId, bool broadcastMetadata = false)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        ScheduleJoinRetry(userId);
        MpChatLobbyAvatarLifecycleHost.QueuePlayerJoinAvatarWork(userId, broadcastMetadata);

        if (MpChatPerformanceGate.ShouldDeferIncomingAvatarData)
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
        if (MpChatPerformanceGate.ShouldDeferIncomingAvatarData)
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

        _wasDeferringIncomingAvatarData = false;
        _lastForcedMetadataRealtime = -999f;
        MpCustomAvatarLobbyTransferManager.ClearLobbyAvatarTransferMemoryCaches();
    }

    public static void ResetSessionAvatarSyncState()
    {
        ClearAllRemotes();
        if (Instance != null)
            Instance.ClearBroadcastDedupeState();
    }

    // Full teardown when the multiplayer session is no longer connected.
    public static void FlushLobbyCustomAvatarsOnServerLeaveIfDisconnected()
    {
        if (!MpChatFeatures.LobbyCustomAvatars)
            return;

        try
        {
            if (MpLobbySessionExit.IsSessionConnected())
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
        StopLobbyScopeBroadcastIfActive();
        ResetSessionAvatarSyncState();
        MpCustomAvatarScaleSource.InvalidateCachedManager();
    }

    private static void StopLobbyScopeBroadcastIfActive()
    {
        var lobby = _lobbyScopeSyncManager;
        if (lobby == null || lobby._broadcastRoutine == null)
            return;

        lobby.StopCoroutine(lobby._broadcastRoutine);
        lobby._broadcastRoutine = null;
    }

    private static float _lastMissingRemoteScanRealtime = -999f;

    private const float MissingRemoteScanCooldownSeconds = 2.5f;

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
        _lastForcedMetadataRealtime = -999f;
        if (Instance != null)
            Instance.ClearBroadcastDedupeState();
    }

    public static void BroadcastMetadataNow(bool applySavedEyeHeight = true, bool forceSend = false)
    {
        if (Instance == null)
            return;

        if (forceSend)
        {
            var now = Time.realtimeSinceStartup;
            if (now - _lastForcedMetadataRealtime < ForcedMetadataMinIntervalSeconds)
                return;

            _lastForcedMetadataRealtime = now;
        }

        Instance.TryBroadcastMetadata(applySavedEyeHeight, forceSend);
    }

    public static void BroadcastScaleOnlyNow()
    {
        if (Instance != null)
            Instance.TryBroadcastScaleOnly();
    }

    // Height calibrate: scale packet first so peers apply height before a full metadata/spawn refresh.
    public static void BroadcastScaleThenMetadata()
    {
        BroadcastScaleOnlyNow();
        BroadcastMetadataNow();
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

        if (_broadcastRoutine != null)
        {
            StopCoroutine(_broadcastRoutine);
            _broadcastRoutine = null;
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

    private void StartBroadcastLoop()
    {
        if (_broadcastRoutine != null)
            StopCoroutine(_broadcastRoutine);
        _broadcastRoutine = StartCoroutine(BroadcastLoop());
    }

    private IEnumerator BroadcastLoop()
    {
        while (true)
        {
            yield return MetadataWait;
            if (!MpChatLobbyDiagnostics.MultiplayerAvatarSyncContextActive(_sessionManager))
                continue;

            TryBroadcastMetadata();
            if (!MpChatPerformanceGate.ShouldBlockAvatarHeavyWork &&
                MpChatLobbyDiagnostics.LobbyHierarchyLooksLikeMultiplayerLobby())
            {
                PollDeferredAvatarUpdates();
                PollJoinRetries();
                MpCustomAvatarLobbyTransferManager.PollDeferredCacheWrites();
                MpCustomAvatarLobbyTransferManager.PollDeferredOutbound();
            }
        }
    }

    private void ClearBroadcastDedupeState()
    {
        _lastSentDescriptor = null;
        _lastSentScale = 1f;
        _lastSendRealtime = 0f;
    }

    private void TryBroadcastMetadata(bool applySavedEyeHeight = true, bool forceSend = false)
    {
        if (!MpChatFeatures.LobbyCustomAvatars)
            return;
        if (!ModSettings.EnableLobbyCustomAvatars)
            return;

        if (!forceSend && MpChatPerformanceGate.ShouldBlockAvatarHeavyWork)
            return;

        if (!forceSend && MpChatPerformanceGate.IsMultiplayerSceneTransitionLikely())
            return;

        // Arena/GameCore: metadata is learned in lobby; no periodic relay while in song or between rounds.
        if (!forceSend &&
            MpChatLobbyDiagnostics.AnyGameCoreLoaded() &&
            !MpChatLobbyDiagnostics.LobbyHierarchyLooksLikeMultiplayerLobby())
            return;

        if (!ReferenceEquals(Instance, this))
            return;

        if (applySavedEyeHeight)
            MpCustomAvatarHeightCalibration.ApplySavedPresetIfAny();

        var local = _sessionManager.localPlayer;
        if (local == null || string.IsNullOrEmpty(local.userId))
            return;

        var descriptor = ModSettings.LobbyCustomAvatarContentHash.Trim().ToUpperInvariant();
        if (!CustomAvatarHashUtil.LooksLikeMd5Hex(descriptor))
            return;
        if (descriptor.Length > MpCustomAvatarPosePacket.MaxDescriptorChars)
            descriptor = descriptor.Substring(0, MpCustomAvatarPosePacket.MaxDescriptorChars);

        var scale = 1f;
        if (!MpCustomAvatarScaleSource.TryGetLocalAvatarScale(out scale))
            scale = 1f;

        var now = Time.realtimeSinceStartup;
        var needsKeepalive =
            !string.IsNullOrEmpty(descriptor) &&
            now - _lastSendRealtime >= MetadataKeepaliveSeconds;

        if (!forceSend &&
            !needsKeepalive &&
            string.Equals(descriptor, _lastSentDescriptor, StringComparison.OrdinalIgnoreCase) &&
            Mathf.Abs(scale - _lastSentScale) <= ScaleEpsilon)
            return;

        byte flags = 0;
        if (!string.IsNullOrEmpty(descriptor))
            flags |= MpCustomAvatarPosePacket.FlagHasDescriptor;
        flags |= MpCustomAvatarPosePacket.FlagHasScale;

        _outboundPacket.Flags = flags;
        _outboundPacket.AvatarDescriptorId = string.IsNullOrEmpty(descriptor) ? null : descriptor;
        _outboundPacket.AvatarScale = scale;
        _outboundPacket.FbtBlob = null;

        try
        {
            _sessionManager.Send(_outboundPacket);
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Send failed: {ex.Message}");
            return;
        }

        _lastSentDescriptor = descriptor;
        _lastSentScale = scale;
        _lastSendRealtime = now;
    }

    private void TryBroadcastScaleOnly()
    {
        if (!MpChatFeatures.LobbyCustomAvatars)
            return;
        if (!ModSettings.EnableLobbyCustomAvatars)
            return;
        if (!ReferenceEquals(Instance, this))
            return;

        var local = _sessionManager.localPlayer;
        if (local == null || string.IsNullOrEmpty(local.userId))
            return;

        var scale = 1f;
        if (!MpCustomAvatarScaleSource.TryGetLocalAvatarScale(out scale))
            scale = 1f;

        if (Mathf.Abs(scale - _lastSentScale) <= ScaleEpsilon)
            return;

        _outboundPacket.Flags = MpCustomAvatarPosePacket.FlagHasScale;
        _outboundPacket.AvatarDescriptorId = null;
        _outboundPacket.AvatarScale = scale;
        _outboundPacket.FbtBlob = null;

        try
        {
            _sessionManager.Send(_outboundPacket);
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Scale send failed: {ex.Message}");
            return;
        }

        _lastSentScale = scale;
        _lastSendRealtime = Time.realtimeSinceStartup;
    }
}

public sealed class MpCustomAvatarRemoteState
{
    public string? AvatarDescriptorId;

    public float AvatarScale = 1f;

    public float ReceivedAtRealtime;

    public bool PendingScaleRespawn;
}
