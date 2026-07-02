using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using CustomAvatar.Avatar;
using CustomAvatar.Player;
using MultiplayerChat.Settings;
using MultiplayerCore.Models;
using MultiplayerCore.Networking;
using UnityEngine;
using Zenject;

namespace MultiplayerChat.Core;

public sealed class MpChatLobbyCustomAvatarDriver : MonoBehaviour
{
    private const string MirrorUserIdPrefix = "Mirror#";
    private const float ScaleEpsilon = 0.002f;

    private AvatarSpawner _avatarSpawner = null!;
    private AvatarLoader _avatarLoader = null!;
    private IConnectedPlayer _connectedPlayer = null!;
    private BeatSaber.AvatarCore.MultiplayerAvatarPoseController _poseController = null!;
    private IMultiplayerSessionManager _sessionManager = null!;

    private MpChatLobbyLivePoseInput? _avatarInput;
    private SpawnedAvatar? _spawnedAvatar;
    private Coroutine? _loadCoroutine;

    private string? _lastSpawnedHash;
    private float _lastAppliedScale = 1f;
    private bool _eventSubscribed;
    private string? _pendingHash;

    private float _pendingScale = 1f;

    private bool _pendingBypassPedestalDefer;

    private bool _loadBypassesPedestalDefer;

    private bool _finalizeComplete;

    private bool _loggedMissingArenaDeps;
    private bool _handingOffSpawn;
    private bool _arenaIkEnabled;

    private float _nextArenaMaintainRealtime;

    private const float ArenaMaintainIntervalSeconds = 0.75f;

    private const float ArenaGameplayMaintainIntervalSeconds = 3f;

    private float _nextLobbyVisualMaintainRealtime;

    private float _nextLobbySpawnRetryRealtime;

    private static int _lobbyAvatarLoadsInFlight;

    private const int MaxConcurrentLobbyAvatarLoads = 1;

    private static readonly List<MpChatLobbyCustomAvatarDriver> StaggerRefreshScratch = new(16);

    private bool _holdsLobbyLoadSlot;

    private string? _registryIndexedUserId;

    internal bool HasActiveCustomAvatar => _spawnedAvatar != null;

    internal string RegistryUserId => _connectedPlayer?.userId ?? "";

    internal bool IsArenaContextForRegistry() => IsArenaContext();

    internal bool IsResultsPedestalContext()
    {
        if (!string.Equals(gameObject.scene.name, "GameCore", StringComparison.Ordinal))
            return false;

        return GetComponent<MultiplayerLobbyAvatarController>() != null ||
               GetComponentInParent<MultiplayerLobbyAvatarController>() != null;
    }

    internal bool IsMirrorPedestalForRegistry() => IsMirrorPedestal();

    internal void ApplyResolvedDependencies(
        AvatarSpawner avatarSpawner,
        AvatarLoader avatarLoader,
        IConnectedPlayer connectedPlayer,
        BeatSaber.AvatarCore.MultiplayerAvatarPoseController poseController,
        IMultiplayerSessionManager sessionManager)
    {
        _avatarSpawner = avatarSpawner;
        _avatarLoader = avatarLoader;
        var previousUserId = _registryIndexedUserId;
        _connectedPlayer = connectedPlayer;
        _poseController = poseController;
        _sessionManager = sessionManager;
        _loggedMissingArenaDeps = false;
        if (isActiveAndEnabled)
        {
            MpChatLobbyCustomAvatarDriverRegistry.Reindex(this, previousUserId);
            _registryIndexedUserId = RegistryUserId;
        }
    }

    internal void SyncArenaPose(BeatSaber.AvatarCore.MultiplayerAvatarPoseController pose)
    {
        if (pose == null || _poseController == pose)
            return;

        _poseController = pose;

        if (_avatarInput != null)
        {
            _avatarInput.Retarget(pose);
        }
        else
        {
            _avatarInput = new MpChatLobbyLivePoseInput(pose);
            _avatarInput.EnableLocalCustomAvatarTracking(
                MpChatFeatures.LobbyUseCustomAvatarTrackingRig && ShouldUseLocalAvatarSettingsHash());
        }

        if (_spawnedAvatar?.gameObject != null)
            TryReparentSpawnToActiveArenaPose();
    }

    internal void HandOffSpawnTo(MpChatLobbyCustomAvatarDriver target)
    {
        if (target == null || target == this)
            return;

        _handingOffSpawn = true;
        target.AdoptSpawnFrom(this);
        ClearSpawnReferencesOnly();
    }

    internal void AdoptSpawnFrom(MpChatLobbyCustomAvatarDriver source)
    {
        if (source._spawnedAvatar == null)
            return;

        if (_spawnedAvatar != null && _spawnedAvatar != source._spawnedAvatar)
            DestroySpawned();

        _spawnedAvatar = source._spawnedAvatar;
        _avatarInput = source._avatarInput;
        _lastSpawnedHash = source._lastSpawnedHash;
        _lastAppliedScale = source._lastAppliedScale;
        _finalizeComplete = source._finalizeComplete;
        _pendingHash = source._pendingHash;

        if (_avatarSpawner == null && source._avatarSpawner != null)
            _avatarSpawner = source._avatarSpawner;
        if (_avatarLoader == null && source._avatarLoader != null)
            _avatarLoader = source._avatarLoader;
        if (_connectedPlayer == null && source._connectedPlayer != null)
            _connectedPlayer = source._connectedPlayer;
        if (_sessionManager == null && source._sessionManager != null)
            _sessionManager = source._sessionManager;

        var facadeRoot = MpChatArenaFacadeRoots.FindFrom(transform);
        if (facadeRoot != null)
        {
            var pose = MpChatArenaAvatarAttach.SelectArenaPose(facadeRoot);
            if (pose != null)
                SyncArenaPose(pose);
        }
    }

    private void ClearSpawnReferencesOnly()
    {
        _spawnedAvatar = null;
        _avatarInput = null;
        _lastSpawnedHash = null;
        _lastAppliedScale = 1f;
        _finalizeComplete = false;
        _pendingHash = null;

        if (_loadCoroutine != null)
        {
            StopCoroutine(_loadCoroutine);
            EndLoadCoroutine();
        }
    }

    [Inject]
    public void Construct(
        AvatarSpawner avatarSpawner,
        AvatarLoader avatarLoader,
        IConnectedPlayer connectedPlayer,
        BeatSaber.AvatarCore.MultiplayerAvatarPoseController poseController,
        IMultiplayerSessionManager sessionManager)
    {
        _avatarSpawner = avatarSpawner;
        _avatarLoader = avatarLoader;
        var previousUserId = _registryIndexedUserId;
        _connectedPlayer = connectedPlayer;
        _poseController = poseController;
        _sessionManager = sessionManager;
        if (isActiveAndEnabled)
        {
            MpChatLobbyCustomAvatarDriverRegistry.Reindex(this, previousUserId);
            _registryIndexedUserId = RegistryUserId;
        }
    }

    private bool DependenciesReady =>
        _avatarSpawner != null &&
        _avatarLoader != null &&
        _connectedPlayer != null &&
        _poseController != null &&
        _sessionManager != null;

    internal bool TryGetLocalUserId(out string localUserId)
    {
        localUserId = "";
        if (_sessionManager?.localPlayer == null)
            return false;

        localUserId = _sessionManager.localPlayer.userId ?? "";
        return !string.IsNullOrEmpty(localUserId);
    }

    // Mirror pedestal previews your avatar; your own lobby/arena slot is for others to see on their client.
    private bool SkipStartupAsLocalPlayerSlot() =>
        IsDisplayingLocalPlayer() && !IsMirrorPedestal();

    private bool UsesLocalAvatarHash() => IsMirrorPedestal();

    private bool IsDisplayingLocalPlayer()
    {
        if (_sessionManager == null || _connectedPlayer == null)
            return false;

        var lp = _sessionManager.localPlayer;
        return lp != null && lp.userId == _connectedPlayer.userId;
    }

    private bool ShouldUseLocalAvatarSettingsHash() => UsesLocalAvatarHash() || IsDisplayingLocalPlayer();

    private bool IsMirrorPedestal() =>
        _connectedPlayer != null &&
        !string.IsNullOrEmpty(_connectedPlayer.userId) &&
        _connectedPlayer.userId.StartsWith(MirrorUserIdPrefix, StringComparison.Ordinal);

    private string MirrorSourceUserId =>
        IsMirrorPedestal() ? _connectedPlayer.userId.Substring(MirrorUserIdPrefix.Length) : _connectedPlayer.userId;

    internal static void NotifyLocalAvatarSettingsChanged()
    {
        MpChatLobbyCustomAvatarDriverRegistry.ForMirrorDrivers(driver =>
            driver.ForceRefresh(forceRespawn: true, bypassPedestalDefer: true));
    }

    internal static void ApplyLocalScaleToMirrorPedestals()
    {
        if (!MpCustomAvatarScaleSource.TryGetLocalAvatarScale(out var scale))
            return;

        MpChatLobbyCustomAvatarDriverRegistry.ForMirrorDrivers(driver =>
            driver.ApplyScaleOnly(scale));
    }

    internal static void HandlePlayerJoined(string userId) =>
        MpChatLobbyAvatarLifecycleHost.QueuePlayerJoinAvatarWork(userId);

    internal static void ProcessPlayerJoinedImmediate(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        MpChatLobbyCustomAvatarDriverRegistry.ForUser(userId, driver =>
        {
            driver.ResetJoinSpawnTracking();
            driver.RefreshFromSyncState(bypassPedestalDefer: true);
        }, lobbyPedestalsOnly: true);
    }

    internal static void HandlePlayerDisconnected(string userId) =>
        MpChatLobbyAvatarLifecycleHost.QueuePlayerLeaveAvatarWork(userId);

    internal static void ProcessPlayerDisconnectedImmediate(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        MpChatLobbyCustomAvatarDriverRegistry.ForUser(userId, driver =>
            driver.HandleRemotePlayerLeft(), lobbyPedestalsOnly: true);
    }

    internal static void FlushAllOnServerLeave()
    {
        _lobbyAvatarLoadsInFlight = 0;
        MpChatLobbyCustomAvatarDriverRegistry.ForAll(driver => driver.FlushForServerLeave());
    }

    internal void FlushForServerLeave()
    {
        if (_loadCoroutine != null)
        {
            StopCoroutine(_loadCoroutine);
            EndLoadCoroutine();
        }

        _pendingHash = null;
        _pendingScale = 1f;
        _pendingBypassPedestalDefer = false;
        _loadBypassesPedestalDefer = false;

        if (_eventSubscribed)
        {
            MpCustomAvatarSyncManager.RemoteLobbyAvatarUpdated -= OnRemoteLobbyAvatarUpdated;
            MpCustomAvatarLobbyTransferManager.LobbyAvatarFileCached -= OnLobbyAvatarFileCached;
            _eventSubscribed = false;
        }

        RestoreVanillaFallback();
    }

    internal void HandleRemotePlayerLeft()
    {
        ResetJoinSpawnTracking();

        if (_loadCoroutine != null)
        {
            StopCoroutine(_loadCoroutine);
            EndLoadCoroutine();
        }

        if (_spawnedAvatar == null)
            return;

        RestoreVanillaFallback();
    }

    internal static bool TryCompleteJoinRefresh(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return true;

        var foundPedestal = false;
        var satisfied = true;

        MpChatLobbyCustomAvatarDriverRegistry.ForUser(userId, driver =>
        {
            foundPedestal = true;
            if (driver.IsLoadingOrSpawned)
                return;

            if (!MpCustomAvatarSyncManager.TryGetRemoteState(userId, out var row) ||
                string.IsNullOrEmpty(row.AvatarDescriptorId) ||
                CustomAvatarInstallListing.IsVanillaDescriptorHash(row.AvatarDescriptorId))
            {
                satisfied = false;
                return;
            }

            satisfied = false;
            driver.ResetJoinSpawnTracking();
            driver.RefreshFromSyncState(bypassPedestalDefer: true);
        }, lobbyPedestalsOnly: true);

        return foundPedestal && satisfied;
    }

    internal static bool AnyPedestalNeedsSpawn(string userId, string descriptorHash)
    {
        if (string.IsNullOrEmpty(userId))
            return false;

        descriptorHash = descriptorHash.Trim().ToUpperInvariant();
        var needsSpawn = false;
        var loading = false;

        MpChatLobbyCustomAvatarDriverRegistry.ForUser(userId, driver =>
        {
            if (driver._loadCoroutine != null)
            {
                loading = true;
                return;
            }

            if (driver._spawnedAvatar == null)
            {
                needsSpawn = true;
                return;
            }

            if (!string.Equals(driver._lastSpawnedHash, descriptorHash, StringComparison.OrdinalIgnoreCase))
                needsSpawn = true;
        }, lobbyPedestalsOnly: true);

        if (loading)
            return false;

        return needsSpawn;
    }

    internal bool MatchesConnectedUser(string userId) =>
        _connectedPlayer != null && _connectedPlayer.userId == userId;

    internal bool IsLoadingOrSpawned => _spawnedAvatar != null || _loadCoroutine != null;

    internal void ResetJoinSpawnTracking()
    {
        _lastSpawnedHash = null;
        _finalizeComplete = false;
    }

    internal void PrepareForJoinRefresh()
    {
        if (_spawnedAvatar == null)
            ResetJoinSpawnTracking();
    }

    internal static void RefreshAllLobbyAvatarDrivers(bool forceRespawn)
    {
        MpChatLobbyCustomAvatarDriverRegistry.ForAllLobbyPedestals(driver =>
            driver.ForceRefresh(forceRespawn, bypassPedestalDefer: true));
    }

    internal static void ReregisterAllPosePolls()
    {
        MpChatLobbyCustomAvatarDriverRegistry.ForAllLobbyPedestals(driver =>
            driver.ReregisterPosePollIfSpawned());
    }

    internal void ReregisterPosePollIfSpawned()
    {
        if (_spawnedAvatar != null && _avatarInput != null)
            _avatarInput.RegisterForPoll();
    }

    internal static IEnumerator RefreshAllLobbyPedestalsStaggered(bool forceRespawn)
    {
        MpChatLobbyCustomAvatarDriverRegistry.CollectLobbyPedestals(StaggerRefreshScratch);
        for (var i = 0; i < StaggerRefreshScratch.Count; i++)
        {
            var driver = StaggerRefreshScratch[i];
            if (driver == null || !driver.isActiveAndEnabled)
                continue;

            driver.ForceRefresh(forceRespawn, bypassPedestalDefer: true);
            yield return null;
            yield return new WaitForSecondsRealtime(0.15f);
        }
    }

    private bool IsArenaContext() =>
        string.Equals(gameObject.scene.name, "GameCore", StringComparison.Ordinal) &&
        !IsResultsPedestalContext();

    private Transform? GetFacadeRoot() =>
        MpChatArenaFacadeRoots.FindFrom(_poseController != null ? _poseController.transform : transform);

    private void ForceRefresh(bool forceRespawn, bool bypassPedestalDefer = false)
    {
        if (!TryEnsureDependencies())
            return;

        if (forceRespawn)
        {
            _lastSpawnedHash = null;
            _finalizeComplete = false;
        }

        if (UsesLocalAvatarHash())
            RefreshFromLocalAvatarSettings(bypassPedestalDefer);
        else
            RefreshFromSyncState(bypassPedestalDefer);
    }

    private void ApplyScaleOnly(float scale)
    {
        if (!TryEnsureDependencies())
            return;

        scale = Mathf.Clamp(scale, 0.25f, 4f);
        if (_spawnedAvatar != null && _spawnedAvatar.gameObject != null)
        {
            TryApplyRemoteScale(scale);
            return;
        }

        ForceRefresh(forceRespawn: false, bypassPedestalDefer: true);
    }

    private void OnEnable()
    {
        MpChatLobbyCustomAvatarDriverRegistry.Register(this);
        _registryIndexedUserId = RegistryUserId;
        TryBeginStartup();
    }

    private void Start() => TryBeginStartup();

    internal void TryBeginStartup()
    {
        if (!MpChatFeatures.LobbyCustomAvatars || !ModSettings.EnableLobbyCustomAvatars)
            return;

        if (!TryEnsureDependencies())
        {
            if (IsArenaContext() && !_loggedMissingArenaDeps)
            {
                _loggedMissingArenaDeps = true;
                MultiplayerChat.Plugin.Log?.Warn(
                    $"[MPChat][LobbyAvatar] Arena driver waiting for Zenject deps on {gameObject.name}");
            }

            return;
        }

        if (SkipStartupAsLocalPlayerSlot())
            return;

        if (_eventSubscribed)
        {
            if (_spawnedAvatar != null && IsArenaContext() && _poseController != null)
            {
                var spawnedGo = _spawnedAvatar.gameObject;
                var facadeRoot = GetFacadeRoot();
                if (spawnedGo != null && facadeRoot != null)
                {
                    MpChatLobbyPedestalVisual.ReapplyArenaSpawnedVisibility(
                        _poseController.transform, facadeRoot, spawnedGo, _lastAppliedScale);
                    MpChatLobbyPedestalVisual.ApplyArenaCustomAvatarVisibility(
                        _poseController.transform, facadeRoot, _avatarInput, spawnedGo.transform);
                }
            }

            if (UsesLocalAvatarHash())
                RefreshFromLocalAvatarSettings();
            else
                RefreshFromSyncState();
            return;
        }

        MpCustomAvatarSyncManager.RemoteLobbyAvatarUpdated += OnRemoteLobbyAvatarUpdated;
        MpCustomAvatarLobbyTransferManager.LobbyAvatarFileCached += OnLobbyAvatarFileCached;
        _eventSubscribed = true;

        if (IsArenaContext())
        {
            MultiplayerChat.Plugin.Log?.Debug(
                $"[MPChat][LobbyAvatar] Arena custom avatar driver ready for {_connectedPlayer.userId}");
        }

        if (UsesLocalAvatarHash())
            RefreshFromLocalAvatarSettings();
        else
            RefreshFromSyncState();
    }

    private bool TryEnsureDependencies()
    {
        if (IsArenaContext())
        {
            var facadeRoot = MpChatArenaFacadeRoots.FindFrom(transform);
            if (facadeRoot != null)
            {
                var arenaPose = MpChatArenaAvatarAttach.SelectArenaPose(facadeRoot);
                if (arenaPose != null)
                    SyncArenaPose(arenaPose);
            }
        }

        _poseController ??= GetComponent<BeatSaber.AvatarCore.MultiplayerAvatarPoseController>();

        if (!DependenciesReady)
        {
            var facadeRoot = MpChatArenaFacadeRoots.FindFrom(transform);
            if (facadeRoot != null)
                MpChatLobbyAvatarZenject.TryInjectFromFacadeRoot(facadeRoot, this);
            else
                MpChatLobbyAvatarZenject.TryInject(this);
        }

        if (!DependenciesReady && IsArenaContext())
        {
            var facadeRoot = MpChatArenaFacadeRoots.FindFrom(transform);
            if (facadeRoot != null)
                MpChatArenaDependencyResolver.TryFill(this, facadeRoot);
        }

        return DependenciesReady;
    }

    private void OnDisable()
    {
        // Arena facade/pose toggles during intro; keep spawn and sync hooks until the facade is destroyed.
        if (IsArenaContext())
        {
            if (_loadCoroutine != null)
            {
                StopCoroutine(_loadCoroutine);
                EndLoadCoroutine();
            }

            return;
        }

        if (_eventSubscribed)
        {
            MpCustomAvatarSyncManager.RemoteLobbyAvatarUpdated -= OnRemoteLobbyAvatarUpdated;
            MpCustomAvatarLobbyTransferManager.LobbyAvatarFileCached -= OnLobbyAvatarFileCached;
            _eventSubscribed = false;
        }

        if (_loadCoroutine != null)
        {
            StopCoroutine(_loadCoroutine);
            EndLoadCoroutine();
        }

        DestroySpawned();

        if (_poseController != null)
            MpChatLobbyPedestalVisual.ShowVanillaRig(_avatarInput, _poseController.transform);
    }

    private void OnDestroy()
    {
        MpChatLobbyCustomAvatarDriverRegistry.Unregister(this);

        if (_loadCoroutine != null)
        {
            StopCoroutine(_loadCoroutine);
            EndLoadCoroutine();
        }

        if (!IsArenaContext())
            return;

        if (_eventSubscribed)
        {
            MpCustomAvatarSyncManager.RemoteLobbyAvatarUpdated -= OnRemoteLobbyAvatarUpdated;
            MpCustomAvatarLobbyTransferManager.LobbyAvatarFileCached -= OnLobbyAvatarFileCached;
            _eventSubscribed = false;
        }

        if (_handingOffSpawn)
        {
            ClearSpawnReferencesOnly();
            return;
        }

        DestroySpawned();
    }

    private void OnRemoteLobbyAvatarUpdated(string userId)
    {
        if (IsMirrorPedestal())
        {
            var lp = _sessionManager.localPlayer;
            if (lp != null && (userId == lp.userId || userId == MirrorSourceUserId))
                RefreshFromLocalAvatarSettings();
            return;
        }

        if (userId != _connectedPlayer.userId)
            return;

        if (_spawnedAvatar == null)
            PrepareForJoinRefresh();

        if (MpCustomAvatarSyncManager.TryGetRemoteState(userId, out var row))
        {
            var remoteHash = row.AvatarDescriptorId?.Trim().ToUpperInvariant() ?? "";
            if (!string.Equals(_lastSpawnedHash, remoteHash, StringComparison.OrdinalIgnoreCase))
            {
                _lastSpawnedHash = null;
                _finalizeComplete = false;
            }
        }

        if (MpCustomAvatarSyncManager.TryConsumePendingScaleRespawn(userId))
        {
            _lastSpawnedHash = null;
            _finalizeComplete = false;
        }

        RefreshFromSyncState();
    }

    private void OnLobbyAvatarFileCached(string hash)
    {
        if (string.IsNullOrEmpty(_pendingHash))
            return;
        if (!string.Equals(_pendingHash, hash, StringComparison.OrdinalIgnoreCase))
            return;
        RefreshFromSyncState();
    }

    private void RefreshFromLocalAvatarSettings(bool bypassPedestalDefer = false)
    {
        if (!MpChatFeatures.LobbyCustomAvatars || !ModSettings.EnableLobbyCustomAvatars || !ShouldUseLocalAvatarSettingsHash())
            return;

        MpCustomAvatarHeightCalibration.ApplySavedPresetIfAny();

        var hash = ModSettings.LobbyCustomAvatarContentHash.Trim().ToUpperInvariant();
        if (!CustomAvatarHashUtil.LooksLikeMd5Hex(hash) ||
            CustomAvatarInstallListing.IsVanillaDescriptorHash(hash))
        {
            RestoreVanillaFallback();
            return;
        }

        var scale = 1f;
        if (!MpCustomAvatarScaleSource.TryGetLocalAvatarScale(out scale))
            scale = 1f;

        BeginLoadForHash(hash, Mathf.Clamp(scale, 0.25f, 4f), bypassPedestalDefer);
    }

    internal void KickArenaFromRemoteSync() => KickFromRemoteSync();

    internal void KickFromRemoteSync()
    {
        if (!IsArenaContext() && !IsResultsPedestalContext())
            return;

        TryBeginStartup();
        RefreshFromSyncState(bypassPedestalDefer: true);
    }

    private void RefreshFromSyncState(bool bypassPedestalDefer = false)
    {
        if (!MpChatFeatures.LobbyCustomAvatars || !ModSettings.EnableLobbyCustomAvatars || ShouldUseLocalAvatarSettingsHash())
            return;

        if (MpChatPerformanceGate.ShouldBlockAvatarHeavyWorkForDriver(IsArenaContext()))
            return;

        if (!IsArenaContext() &&
            !IsResultsPedestalContext() &&
            !bypassPedestalDefer &&
            MpChatPerformanceGate.ShouldDeferLobbyPedestalAvatarRefresh)
            return;

        if (!MpCustomAvatarSyncManager.TryGetRemoteState(_connectedPlayer.userId, out var row))
        {
            if (_spawnedAvatar == null && _loadCoroutine == null)
            {
                MpCustomAvatarSyncManager.ScheduleJoinRetry(_connectedPlayer.userId);
                return;
            }

            RestoreVanillaFallback();
            return;
        }

        var hash = row.AvatarDescriptorId?.Trim().ToUpperInvariant() ?? "";
        if (!CustomAvatarHashUtil.LooksLikeMd5Hex(hash))
        {
            RestoreVanillaFallback();
            return;
        }

        if (CustomAvatarInstallListing.IsVanillaDescriptorHash(hash))
        {
            RestoreVanillaFallback();
            return;
        }

        if (IsArenaContext() && !CanAttemptArenaSpawn())
            return;

        BeginLoadForHash(hash, Mathf.Clamp(row.AvatarScale, 0.25f, 4f), bypassPedestalDefer);
    }

    internal void PromoteArenaAfterIntro()
    {
        if (!IsArenaContext() || !DependenciesReady || _poseController == null)
            return;

        TryReparentSpawnToActiveArenaPose();
        TryPromoteArenaIkIfReady();

        if (_spawnedAvatar == null && _loadCoroutine == null)
        {
            if (UsesLocalAvatarHash())
                RefreshFromLocalAvatarSettings();
            else
                RefreshFromSyncState();
            return;
        }

        if (_spawnedAvatar?.gameObject == null)
            return;

        var facadeRoot = GetFacadeRoot();
        if (facadeRoot == null)
            return;

        MpChatLobbyPedestalVisual.ReapplyArenaSpawnedVisibility(
            _poseController.transform, facadeRoot, _spawnedAvatar.gameObject, _lastAppliedScale);
        MpChatLobbyPedestalVisual.ApplyArenaCustomAvatarVisibility(
            _poseController.transform, facadeRoot, _avatarInput, _spawnedAvatar.gameObject.transform);
    }

    private bool IsArenaPoseReadyForSpawn() =>
        _poseController != null &&
        MpChatArenaAvatarAttach.IsArenaPoseReadyForCustomSpawn(_poseController);

    private bool CanAttemptArenaSpawn() =>
        _poseController != null &&
        MpChatArenaAvatarAttach.CanAttemptArenaSpawn(_poseController);

    private void TryReparentSpawnToActiveArenaPose()
    {
        if (!IsArenaContext() || _spawnedAvatar?.gameObject == null || _poseController == null)
            return;

        var facadeRoot = GetFacadeRoot();
        if (facadeRoot == null || !MpChatArenaAvatarAttach.IsArenaPoseReadyForCustomSpawn(_poseController))
            return;

        _spawnedAvatar.gameObject.transform.SetParent(_poseController.transform, false);
    }

    private void TryPromoteArenaIkIfReady()
    {
        if (!IsArenaContext() || _arenaIkEnabled || _spawnedAvatar == null)
            return;
        if (!IsArenaPoseReadyForSpawn())
            return;

        MpCustomAvatarLobbyIk.SetLocomotionEnabled(_spawnedAvatar, true);
        _arenaIkEnabled = true;
    }

    private void BeginLoadForHash(string hash, float scale, bool bypassPedestalDefer = false)
    {
        hash = hash.Trim().ToUpperInvariant();

        if (IsArenaContext() && MpChatAvatarWorkloadGate.ShouldDeferArenaAvatarSpawn)
        {
            _pendingHash = hash;
            _pendingScale = scale;
            _pendingBypassPedestalDefer = bypassPedestalDefer;
            return;
        }

        if (IsArenaContext())
            MpChatArenaTiming.NotifyArenaSpawnAttempt();

        if (MpChatAvatarWorkloadGate.ShouldDeferAvatarNetworkDiskAndSpawnWork &&
            !IsArenaContext() &&
            !IsResultsPedestalContext())
            return;

        if (MpChatPerformanceGate.ShouldBlockAvatarHeavyWorkForDriver(IsArenaContext()))
            return;

        if (!IsArenaContext() &&
            !IsResultsPedestalContext() &&
            !bypassPedestalDefer &&
            MpChatPerformanceGate.ShouldDeferLobbyPedestalAvatarRefresh)
            return;

        if (string.Equals(_lastSpawnedHash, hash, StringComparison.OrdinalIgnoreCase) &&
            _spawnedAvatar != null &&
            _spawnedAvatar.gameObject != null)
        {
            var existingGo = _spawnedAvatar.gameObject;
            if (existingGo == null)
            {
                _spawnedAvatar = null;
                _lastSpawnedHash = null;
                _finalizeComplete = false;
            }
            else
            {
                TryApplyRemoteScale(scale);
                if (!_finalizeComplete)
                    _loadCoroutine = StartCoroutine(FinishSpawnNextFrame(scale));
                return;
            }
        }

        if (_loadCoroutine != null && string.Equals(_pendingHash, hash, StringComparison.OrdinalIgnoreCase))
            return;

        _pendingHash = hash;
        _finalizeComplete = false;

        if (_loadCoroutine != null)
        {
            StopCoroutine(_loadCoroutine);
            ReleaseLobbyLoadSlot();
            _loadCoroutine = null;
        }

        if (!TryAcquireLobbyLoadSlot())
        {
            _pendingHash = hash;
            _pendingScale = scale;
            _pendingBypassPedestalDefer = bypassPedestalDefer;
            return;
        }

        _loadBypassesPedestalDefer = bypassPedestalDefer;
        _loadCoroutine = StartCoroutine(LoadAndSpawnCoroutine(hash, scale));
    }

    internal void TryResumePendingLoad()
    {
        if (string.IsNullOrEmpty(_pendingHash) || _loadCoroutine != null)
            return;

        if (IsArenaContext() && MpChatAvatarWorkloadGate.ShouldDeferArenaAvatarSpawn)
            return;

        if (MpChatAvatarWorkloadGate.ShouldDeferAvatarNetworkDiskAndSpawnWork &&
            !IsArenaContext() &&
            !IsResultsPedestalContext())
            return;

        if (MpChatPerformanceGate.ShouldBlockAvatarHeavyWorkForDriver(IsArenaContext()))
            return;

        BeginLoadForHash(_pendingHash!, _pendingScale, _pendingBypassPedestalDefer);
    }

    private bool TryAcquireLobbyLoadSlot()
    {
        if (IsArenaContext())
            return true;
        if (_holdsLobbyLoadSlot)
            return true;
        if (_lobbyAvatarLoadsInFlight >= MaxConcurrentLobbyAvatarLoads)
            return false;

        _lobbyAvatarLoadsInFlight++;
        _holdsLobbyLoadSlot = true;
        return true;
    }

    private void ReleaseLobbyLoadSlot()
    {
        if (!_holdsLobbyLoadSlot)
            return;

        _holdsLobbyLoadSlot = false;
        if (_lobbyAvatarLoadsInFlight > 0)
            _lobbyAvatarLoadsInFlight--;

        MpChatLobbyCustomAvatarDriverRegistry.WakePendingLobbyLoads();
    }

    private IEnumerator FinishSpawnNextFrame(float scale)
    {
        yield return null;
        if (_spawnedAvatar == null)
        {
            _loadCoroutine = null;
            yield break;
        }

        FinalizeSpawned(scale);
        _pendingHash = null;
        _loadCoroutine = null;
    }

    private void LateUpdate()
    {
        if (IsArenaContext())
        {
            var facadeRoot = GetFacadeRoot();
            var needsSpawn = _spawnedAvatar == null && _loadCoroutine == null;

            if (facadeRoot != null && (needsSpawn || Time.realtimeSinceStartup >= _nextArenaMaintainRealtime))
            {
                if (!needsSpawn)
                    _nextArenaMaintainRealtime = Time.realtimeSinceStartup + GetArenaMaintainIntervalSeconds();

                if (needsSpawn || !MpChatPerformanceGate.ShouldBlockAvatarHeavyWork)
                {
                    var arenaPose = MpChatArenaAvatarAttach.SelectArenaPose(facadeRoot);
                    if (arenaPose != null)
                        SyncArenaPose(arenaPose);
                }
            }

            if (!DependenciesReady)
                TryBeginStartup();

            if (needsSpawn || Time.realtimeSinceStartup >= _nextArenaMaintainRealtime)
            {
                if (needsSpawn || !MpChatPerformanceGate.ShouldBlockAvatarHeavyWork)
                {
                    TryReparentSpawnToActiveArenaPose();
                    TryPromoteArenaIkIfReady();
                }
            }

            if (needsSpawn && DependenciesReady && CanAttemptArenaSpawn())
            {
                if (UsesLocalAvatarHash())
                    RefreshFromLocalAvatarSettings();
                else
                    RefreshFromSyncState();
            }
        }

        if (!DependenciesReady || _poseController == null)
            return;

        if (_spawnedAvatar == null)
        {
            if (!string.IsNullOrEmpty(_pendingHash) && _loadCoroutine == null)
            {
                TryResumePendingLoad();
                return;
            }

            if (!string.IsNullOrEmpty(_lastSpawnedHash) && _loadCoroutine == null &&
                !MpChatPerformanceGate.ShouldBlockAvatarHeavyWorkForDriver(IsArenaContext()))
            {
                if (!IsArenaContext())
                {
                    if (Time.realtimeSinceStartup < _nextLobbySpawnRetryRealtime)
                        return;

                    _nextLobbySpawnRetryRealtime = Time.realtimeSinceStartup +
                        MpChatLobbyCustomAvatarDriverRegistry.GetLobbySpawnRetryIntervalSeconds();
                }

                _lastSpawnedHash = null;
                _finalizeComplete = false;
                if (UsesLocalAvatarHash())
                    RefreshFromLocalAvatarSettings();
                else
                    RefreshFromSyncState();
            }

            return;
        }

        if (!IsArenaContext() && Time.realtimeSinceStartup < _nextLobbyVisualMaintainRealtime)
            return;

        var spawnedGo = _spawnedAvatar.gameObject;
        if (spawnedGo == null)
        {
            _spawnedAvatar = null;
            return;
        }

        if (IsArenaContext())
        {
            if (Time.realtimeSinceStartup >= _nextArenaMaintainRealtime)
            {
                _nextArenaMaintainRealtime = Time.realtimeSinceStartup + GetArenaMaintainIntervalSeconds();
                var facadeRoot = GetFacadeRoot();
                if (facadeRoot != null)
                {
                    if (MpChatPerformanceGate.ShouldBlockAvatarHeavyWork)
                    {
                        MpChatLobbyPedestalVisual.EnsureSpawnedVisible(spawnedGo, _lastAppliedScale);
                    }
                    else
                    {
                        MpChatLobbyPedestalVisual.ReapplyArenaSpawnedVisibility(
                            _poseController.transform, facadeRoot, spawnedGo, _lastAppliedScale);
                        MpChatLobbyPedestalVisual.ApplyArenaCustomAvatarVisibility(
                            _poseController.transform, facadeRoot, _avatarInput, spawnedGo.transform);
                    }
                }
            }
        }
        else
        {
            _nextLobbyVisualMaintainRealtime = Time.realtimeSinceStartup +
                MpChatLobbyCustomAvatarDriverRegistry.GetLobbyVisualMaintainIntervalSeconds();
            MpChatLobbyPedestalVisual.EnsureSpawnedVisible(spawnedGo, _lastAppliedScale);
            MpChatLobbyPedestalVisual.ApplyCustomAvatarVisibility(_poseController.transform, _avatarInput);
        }
    }

    private static float GetArenaMaintainIntervalSeconds() =>
        MpChatPerformanceGate.ShouldBlockAvatarHeavyWork
            ? ArenaGameplayMaintainIntervalSeconds
            : ArenaMaintainIntervalSeconds;

    private IEnumerator LoadAndSpawnCoroutine(string md5HexUpper, float scale)
    {
        yield return null;

        if (MpChatPerformanceGate.ShouldBlockAvatarHeavyWorkForDriver(IsArenaContext()))
        {
            EndLoadCoroutine();
            yield break;
        }

        if (!IsArenaContext() &&
            !_loadBypassesPedestalDefer &&
            !IsResultsPedestalContext() &&
            MpChatPerformanceGate.ShouldDeferLobbyPedestalAvatarRefresh)
        {
            EndLoadCoroutine();
            yield break;
        }

        if (!TryResolveAvatarFilePath(md5HexUpper, out var path))
        {
            RestoreVanillaFallback();
            var ownerId = IsMirrorPedestal() ? MirrorSourceUserId : _connectedPlayer.userId;
            MpCustomAvatarUserNotifier.PostDownloading(ownerId, _connectedPlayer.userName);
            MpCustomAvatarLobbyTransferManager.RequestLobbyAvatarFile(md5HexUpper, ownerId);
            MultiplayerChat.Plugin.Log?.Info(
                $"[MPChat][LobbyAvatar] Requesting .avatar download for hash {md5HexUpper} from {_connectedPlayer.userId}");
            EndLoadCoroutine();
            yield break;
        }

        System.Threading.Tasks.Task<AvatarPrefab?> task;
        try
        {
            task = _avatarLoader.LoadFromFileAsync(path, null, System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] LoadFromFileAsync threw: {ex.Message}");
            RestoreVanillaFallback();
            EndLoadCoroutine();
            yield break;
        }

        while (!task.IsCompleted)
            yield return null;

        var prefab = task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? task.Result : null;
        if (prefab == null)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Failed loading avatar file: {path}");
            RestoreVanillaFallback();
            EndLoadCoroutine();
            yield break;
        }

        yield return null;

        if (!CreateSpawned(prefab, md5HexUpper))
        {
            RestoreVanillaFallback();
            EndLoadCoroutine();
            yield break;
        }

        // Let Custom Avatars finish activating hierarchy before we hide vanilla rig parts.
        yield return null;

        if (_spawnedAvatar == null)
        {
            RestoreVanillaFallback();
            EndLoadCoroutine();
            yield break;
        }

        FinalizeSpawned(scale);
        _pendingHash = null;
        EndLoadCoroutine();
        MultiplayerChat.Plugin.Log?.Debug(
            $"[MPChat][LobbyAvatar] Remote custom avatar ready for {_connectedPlayer.userId} hash={md5HexUpper}");
    }

    private bool CreateSpawned(AvatarPrefab prefab, string hashUpper)
    {
        if (string.Equals(_lastSpawnedHash, hashUpper, StringComparison.OrdinalIgnoreCase) && _spawnedAvatar != null)
            return true;

        DestroySpawned();
        MpChatLobbyPedestalVisual.PrepareForCustomAvatar(_poseController.transform);

        _avatarInput ??= new MpChatLobbyLivePoseInput(_poseController);
        _avatarInput.EnableLocalCustomAvatarTracking(
            MpChatFeatures.LobbyUseCustomAvatarTrackingRig && ShouldUseLocalAvatarSettingsHash());

        var spawnParent = _poseController.transform;
        if (IsArenaContext())
        {
            var facadeRoot = GetFacadeRoot();
            if (facadeRoot != null)
                spawnParent = MpChatArenaAvatarAttach.GetArenaSpawnParent(facadeRoot, _poseController);
        }

        try
        {
            _spawnedAvatar = _avatarSpawner.SpawnAvatar(prefab, _avatarInput, spawnParent);
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] SpawnAvatar threw: {ex.Message}");
            return false;
        }

        if (_spawnedAvatar == null)
            return false;

        if (IsArenaContext())
        {
            _arenaIkEnabled = false;
            MpCustomAvatarLobbyIk.SetLocomotionEnabled(_spawnedAvatar, false);
        }

        _lastSpawnedHash = hashUpper;
        return true;
    }

    private void FinalizeSpawned(float scale)
    {
        if (_spawnedAvatar == null || _avatarInput == null)
            return;

        try
        {
            scale = Mathf.Clamp(scale, 0.25f, 4f);
            _lastAppliedScale = scale;

            _avatarInput.RegisterForPoll();
            UpdateLocalPoseBridgeTarget();

            _avatarInput.SeedInitialPose();
            if (IsArenaContext())
            {
                _arenaIkEnabled = IsArenaPoseReadyForSpawn();
                MpCustomAvatarLobbyIk.SetLocomotionEnabled(_spawnedAvatar, _arenaIkEnabled);
            }
            else
            {
                MpCustomAvatarLobbyIk.EnableLocomotion(_spawnedAvatar);
            }

            MpCustomAvatarSpawnScale.Apply(_spawnedAvatar, scale);

            var spawnedGo = _spawnedAvatar.gameObject;
            if (spawnedGo == null)
            {
                MultiplayerChat.Plugin.Log?.Warn("[MPChat][LobbyAvatar] SpawnedAvatar has no GameObject after finalize steps");
                RestoreVanillaFallback();
                return;
            }

            if (IsArenaContext())
            {
                var facadeRoot = GetFacadeRoot();
                if (facadeRoot != null)
                {
                    MpChatLobbyPedestalVisual.ReapplyArenaSpawnedVisibility(
                        _poseController.transform, facadeRoot, spawnedGo, _lastAppliedScale);
                    MpChatLobbyPedestalVisual.ApplyArenaCustomAvatarVisibility(
                        _poseController.transform, facadeRoot, _avatarInput, spawnedGo.transform);
                }
            }
            else
            {
                MpChatLobbyPedestalVisual.EnsureSpawnedVisible(spawnedGo, _lastAppliedScale);
                MpChatLobbyPedestalVisual.ApplyCustomAvatarVisibility(_poseController.transform, _avatarInput);
            }

            _finalizeComplete = true;
            var context = IsResultsPedestalContext()
                ? "results"
                : IsArenaContext()
                    ? "arena"
                    : "pedestal";
            MultiplayerChat.Plugin.Log?.Info(
                $"[MPChat][LobbyAvatar] Showing custom avatar on {context} for {_connectedPlayer.userId} (hash {_lastSpawnedHash})");
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] FinalizeSpawned failed: {ex}");
            RestoreVanillaFallback();
        }
    }

    private void EndLoadCoroutine()
    {
        _loadBypassesPedestalDefer = false;
        ReleaseLobbyLoadSlot();
        _loadCoroutine = null;
    }

    private void TryApplyRemoteScale(float scale)
    {
        if (_spawnedAvatar == null)
            return;

        if (Mathf.Abs(_lastAppliedScale - scale) <= ScaleEpsilon)
            return;

        MpCustomAvatarSpawnScale.Apply(_spawnedAvatar, scale);
        _lastAppliedScale = scale;
    }

    private void RestoreVanillaFallback()
    {
        var userId = _connectedPlayer?.userId ?? gameObject.name;
        MultiplayerChat.Plugin.Log?.Debug(
            $"[MPChat][LobbyAvatar] Showing default Beat Saber rig for {userId}");
        _pendingHash = null;
        DestroySpawned();
        if (_poseController != null)
        {
            var restoreRoot = IsArenaContext() ? GetFacadeRoot() ?? _poseController.transform : _poseController.transform;
            MpChatLobbyPedestalVisual.ShowVanillaRig(_avatarInput, restoreRoot);
        }
    }

    private void UpdateLocalPoseBridgeTarget()
    {
        if (!MpChatFeatures.LobbyUseCustomAvatarTrackingRig)
        {
            if (MpChatLocalPlayerPoseBridge.TargetIs(_poseController))
                MpChatLocalPlayerPoseBridge.ClearLocalTarget();
            return;
        }

        if (ShouldUseLocalAvatarSettingsHash() || IsDisplayingLocalPlayer())
            MpChatLocalPlayerPoseBridge.SetLocalTarget(_poseController);
        else if (MpChatLocalPlayerPoseBridge.TargetIs(_poseController))
            MpChatLocalPlayerPoseBridge.ClearLocalTarget();
    }

    private void DestroySpawned()
    {
        if (_avatarInput != null)
        {
            _avatarInput.UnregisterFromPoll();
            if (MpChatLocalPlayerPoseBridge.TargetIs(_poseController))
                MpChatLocalPlayerPoseBridge.ClearLocalTarget();
        }

        if (_spawnedAvatar != null)
        {
            if (_avatarInput != null)
                _avatarInput.SetEnabled(false);

            var go = _spawnedAvatar.gameObject;
            if (go != null)
                Destroy(go);
            _spawnedAvatar = null;
        }

        _lastSpawnedHash = null;
        _lastAppliedScale = 1f;
        _finalizeComplete = false;
        _arenaIkEnabled = false;
    }

    private static bool TryResolveAvatarFilePath(string md5HexUpper, out string path)
    {
        if (CustomAvatarLobbyHashCache.TryGetPath(md5HexUpper, out path))
            return true;

        var rel = ModSettings.LobbyCustomAvatarRelativePath.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(rel))
        {
            path = "";
            return false;
        }

        var full = Path.Combine(BeatSaberPaths.CustomAvatarsDirectory,
            rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            path = "";
            return false;
        }

        try
        {
            if (!string.Equals(CustomAvatarHashUtil.Md5HexFile(full), md5HexUpper, StringComparison.OrdinalIgnoreCase))
            {
                path = "";
                return false;
            }
        }
        catch
        {
            path = "";
            return false;
        }

        path = full;
        return true;
    }
}
