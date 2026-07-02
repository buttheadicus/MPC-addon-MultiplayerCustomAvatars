using System;
using HarmonyLib;
using MultiplayerChat.Contracts;
using MultiplayerChat.Core;
using MultiplayerChat.Core.Addons;
using MultiplayerChat.HarmonyPatches;
using MultiplayerChat.Network;
using MultiplayerChat.Settings;
using MultiplayerCore.Models;
using SiraUtil.Objects.Multiplayer;
using UnityEngine;

namespace MultiplayerChat.Addon.CustomAvatars;

[MpChatAddon(AddonIds.CustomAvatars)]
public sealed class CustomAvatarsAddon : IMpChatAddon, IMpChatLobbyAvatarHook, IMpChatSettingsPage
{
    private IMpChatHost? _host;
    private object? _lifecycleHost;
    private HarmonyLib.Harmony? _harmony;
    private readonly CustomAvatarsLobbyHook _lobbyHook = new();

    public string Id => AddonIds.CustomAvatars;

    public string DisplayName => "Custom Multiplayer Avatars";

    public Version Version => new(1, 0, 0);

    string IMpChatLobbyAvatarHook.AddonId => Id;

    string IMpChatSettingsPage.AddonId => Id;

    public string PageTitle => "Custom Avatars";

    public string SettingsCategory => "Addons";

    public void OnLoad(IMpChatHost host)
    {
        _host = host;
        if (!CustomAvatarDependenciesBootstrap.SessionDependenciesReady)
        {
            host.LogWarn("[MPChat][Addons] customAvatars: dependencies not ready.");
            return;
        }

        _harmony = new HarmonyLib.Harmony($"com.multiplayerchat.addon.{Id}");
        MpChatMultiplayerLobbyScaleAnimatorPatches.Apply(_harmony);
        MpChatArenaAvatarHarmony.Apply(_harmony);

        _lifecycleHost = host.CreatePersistentHost("MPChatLobbyAvatarLifecycleHost");
        if (_lifecycleHost is GameObject lifecycleGo)
        {
            var lifecycle = lifecycleGo.AddComponent<MpChatLobbyAvatarLifecycleHost>();
            host.Inject(lifecycle);
        }

        var syncGo = host.CreatePersistentHost("MPChatCustomAvatarSyncHost");
        if (syncGo is GameObject syncHost)
        {
            var sync = syncHost.AddComponent<MpCustomAvatarSyncManager>();
            host.Inject(sync);
        }

        var transferGo = host.CreatePersistentHost("MPChatCustomAvatarTransferHost");
        if (transferGo is GameObject transferHost)
        {
            var transfer = transferHost.AddComponent<MpCustomAvatarLobbyTransferManager>();
            host.Inject(transfer);
        }

        host.RegisterLobbyAvatarHook(_lobbyHook);
        host.RegisterSettingsPage(this);
        host.RegisterSettingsPresenter(
            Id,
            typeof(MultiplayerChat.UI.CustomAvatarsSettingsFlowCoordinator),
            "Custom Avatars");
        host.RegisterPacketCallback<MpCustomAvatarPosePacket>(OnPosePacket);
        host.RegisterPacketCallback<MpCustomAvatarFileRequestPacket>(OnFileRequestPacket);
        host.RegisterPacketCallback<MpCustomAvatarFileChunkPacket>(OnFileChunkPacket);

        AddonCustomAvatarsBridge.SetHandlers(
            MpCustomAvatarSyncManager.FlushLobbyCustomAvatarsOnServerLeaveIfDisconnected,
            MpCustomAvatarSyncManager.ClearRemote,
            MpCustomAvatarSyncManager.NotifyRemoteAvatarMayBeReady,
            MpCustomAvatarSyncManager.PollDeferredAvatarUpdates,
            MpChatLobbyAvatarLifecycleHost.ScheduleLobbySessionRejoinRefresh,
            MpCustomAvatarSyncManager.FlushLobbyCustomAvatarsOnServerLeave,
            MpCustomAvatarSyncManager.OnVoipPipelineReloaded,
            MpCustomAvatarSyncManager.EnsureActiveLobbyHostAfterArena,
            MpChatLocalCaPoseSampler.TryGetWorldDevicePoses);

        AddonGameplayBridge.SetArenaAttachHandler(MpChatArenaAvatarAttach.RefreshAttachForGameplay);

        host.SetCapability(AddonCapability.LobbyCustomAvatars, true);
        ModPresenceManager.Instance?.RefreshLocalAddonCapabilities(AddonCapability.LobbyCustomAvatars);
    }

    public void OnUnload()
    {
        MpCustomAvatarSyncManager.ClearAllRemotes();
        AddonCustomAvatarsBridge.ClearHandlers();
        AddonGameplayBridge.Clear();
        _host?.UnregisterLobbyAvatarHook(_lobbyHook);
        _host?.UnregisterSettingsPresenter(Id);
        _host?.UnregisterSettingsPage(this);
        _host?.SetCapability(AddonCapability.LobbyCustomAvatars, false);

        if (_lifecycleHost != null)
            _host?.DestroyPersistentHost(_lifecycleHost);
        _host?.DestroyPersistentHost("MPChatCustomAvatarSyncHost");
        _host?.DestroyPersistentHost("MPChatCustomAvatarTransferHost");

        try
        {
            _harmony?.UnpatchSelf();
        }
        catch
        {
            // ignored
        }

        _harmony = null;
        _lifecycleHost = null;
        _host = null;
        ModPresenceManager.Instance?.RefreshLocalAddonCapabilities(AddonCapability.None);
    }

    void IMpChatLobbyAvatarHook.DecorateLobbyAvatar(object lobbyAvatarController) =>
        _lobbyHook.DecorateLobbyAvatar(lobbyAvatarController);

    void IMpChatLobbyAvatarHook.DecorateLobbyAvatarPlace(object lobbyAvatarPlace) =>
        _lobbyHook.DecorateLobbyAvatarPlace(lobbyAvatarPlace);

    private static void OnPosePacket(MpCustomAvatarPosePacket packet, object sender)
    {
        if (sender is not IConnectedPlayer player || string.IsNullOrEmpty(player.userId))
            return;
        MpCustomAvatarSyncManager.ApplyReceived(player.userId, packet);
    }

    private static void OnFileRequestPacket(MpCustomAvatarFileRequestPacket packet, object sender)
    {
        if (sender is not IConnectedPlayer player)
            return;
        MpCustomAvatarLobbyTransferManager.Instance?.HandleFileRequest(packet, player);
    }

    private static void OnFileChunkPacket(MpCustomAvatarFileChunkPacket packet, object sender)
    {
        if (sender is not IConnectedPlayer player)
            return;
        MpCustomAvatarLobbyTransferManager.Instance?.HandleFileChunk(packet, player);
    }
}

internal sealed class CustomAvatarsLobbyHook : IMpChatLobbyAvatarHook
{
    public string AddonId => AddonIds.CustomAvatars;

    public void DecorateLobbyAvatar(object lobbyAvatarController)
    {
        if (lobbyAvatarController is not MultiplayerLobbyAvatarController controller)
            return;

        if (!CustomAvatarDependenciesBootstrap.IsSessionActive())
            return;

        controller.gameObject.AddComponent<MpChatLobbyPedestalScaleGuard>();
        var driver = controller.gameObject.AddComponent<MpChatLobbyCustomAvatarDriver>();
        MpChatLobbyAvatarZenject.TryInject(driver);
    }

    public void DecorateLobbyAvatarPlace(object lobbyAvatarPlace)
    {
    }
}
