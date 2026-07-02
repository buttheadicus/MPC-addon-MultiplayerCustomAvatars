using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MultiplayerChat.Network;
using MultiplayerChat.Settings;
using MultiplayerCore.Models;
using MultiplayerCore.Networking;
using UnityEngine;
using Zenject;

namespace MultiplayerChat.Core;

// Lobby .avatar transfer: on-demand only, short unicast bursts when requested.
public sealed class MpCustomAvatarLobbyTransferManager : MonoBehaviour, IInitializable
{
    public static MpCustomAvatarLobbyTransferManager? Instance { get; private set; }

    public static event Action<string>? LobbyAvatarFileCached;

    private const float RequestCooldownSeconds = 1.2f;

    // Unicast to one requester: up to this many bytes per frame, then yield one frame.
    private const int UnicastBytesBudgetPerFrame = 256 * 1024;

    // Legacy/broadcast fan-out stays slower so large lobbies do not spike the relay.
    private const float MulticastChunkIntervalSeconds = 0.06f;

    private const float FlushCooldownSeconds = 1f;

    private static readonly object Gate = new();

    private static readonly Dictionary<string, float> RequestSentAt =
        new(StringComparer.Ordinal);

    private static readonly Dictionary<string, IncomingAssembly> IncomingByHash =
        new(StringComparer.Ordinal);

    private static readonly HashSet<string> OutboundTransferKeysInFlight = new(StringComparer.Ordinal);

    private static readonly HashSet<string> DownloadNotifiedHashes = new(StringComparer.Ordinal);

    private static readonly HashSet<string> RequestedHashes = new(StringComparer.Ordinal);

    private static readonly Queue<PendingCacheWrite> DeferredCacheWrites = new();

    private static readonly Queue<OutboundJob> DeferredOutboundJobs = new();

    private static readonly Dictionary<string, string> DeferredFileRequestByHash =
        new(StringComparer.OrdinalIgnoreCase);

    private static MpCustomAvatarLobbyTransferManager? _lobbyScopeTransferManager;

    private static float _lastFlushRealtime = -999f;

    [Inject] private readonly IMultiplayerSessionManager _sessionManager = null!;

    private Coroutine? _sendRoutine;

    private readonly Queue<OutboundJob> _sendQueue = new();

    private readonly MpCustomAvatarFileRequestPacket _requestPacket = new();

    private readonly MpCustomAvatarFileChunkPacket _chunkPacket = new();

    private byte[] _chunkScratch = Array.Empty<byte>();

    private Coroutine? _deferredPollRoutine;

    public void Initialize()
    {
        _lobbyScopeTransferManager = this;
        Instance = this;
        _deferredPollRoutine = StartCoroutine(PollDeferredTransferWorkRoutine());
    }

    private void OnDestroy()
    {
        if (_deferredPollRoutine != null)
        {
            StopCoroutine(_deferredPollRoutine);
            _deferredPollRoutine = null;
        }

        if (ReferenceEquals(_lobbyScopeTransferManager, this))
            _lobbyScopeTransferManager = null;

        if (ReferenceEquals(Instance, this))
            Instance = null;
    }

    // Stop arena/transition transfers; queued work runs on FlushDeferredLobbyAvatarFileTransfers unless discarded.
    public static void SuspendLobbyAvatarFileTransfer(bool discardInFlightSendQueue = false)
    {
        var host = _lobbyScopeTransferManager;
        if (host != null)
        {
            if (host._sendRoutine != null)
            {
                host.StopCoroutine(host._sendRoutine);
                host._sendRoutine = null;
            }

            if (discardInFlightSendQueue)
                host._sendQueue.Clear();
            else
            {
                while (host._sendQueue.Count > 0)
                {
                    lock (Gate)
                        DeferredOutboundJobs.Enqueue(host._sendQueue.Dequeue());
                }
            }
        }

        ClearLobbyAvatarTransferMemoryCaches();
    }

    public static void ClearLobbyAvatarTransferMemoryCaches()
    {
        lock (Gate)
        {
            IncomingByHash.Clear();
            DownloadNotifiedHashes.Clear();
            OutboundTransferKeysInFlight.Clear();
            RequestSentAt.Clear();
            RequestedHashes.Clear();
            DeferredCacheWrites.Clear();
            DeferredOutboundJobs.Clear();
            DeferredFileRequestByHash.Clear();
        }
    }

    public static void FlushDeferredLobbyAvatarFileTransfers(bool rescanMissingRemotes = false)
    {
        if (!MpChatPerformanceGate.CanRunLobbyAvatarFileTransfer)
            return;

        var host = _lobbyScopeTransferManager;
        if (host == null)
            return;

        var hasDeferredWork = false;
        lock (Gate)
        {
            hasDeferredWork = DeferredFileRequestByHash.Count > 0 ||
                              DeferredOutboundJobs.Count > 0 ||
                              DeferredCacheWrites.Count > 0;
        }

        if (!hasDeferredWork && !rescanMissingRemotes)
            return;

        var now = Time.realtimeSinceStartup;
        if (now - _lastFlushRealtime < FlushCooldownSeconds && !hasDeferredWork)
            return;

        _lastFlushRealtime = now;
        Instance = host;

        KeyValuePair<string, string>[] fileRequests;
        lock (Gate)
        {
            fileRequests = new KeyValuePair<string, string>[DeferredFileRequestByHash.Count];
            var i = 0;
            foreach (var kvp in DeferredFileRequestByHash)
                fileRequests[i++] = kvp;
            DeferredFileRequestByHash.Clear();
        }

        for (var i = 0; i < fileRequests.Length; i++)
            RequestLobbyAvatarFile(fileRequests[i].Key, fileRequests[i].Value);

        PollDeferredOutbound();
        PollDeferredCacheWrites();

        if (rescanMissingRemotes)
            MpCustomAvatarSyncManager.RequestMissingRemoteAvatarFiles();
    }

    private static IEnumerator PollDeferredTransferWorkRoutine()
    {
        var wait = new WaitForSeconds(0.35f);
        while (true)
        {
            yield return wait;
            if (!MpChatFeatures.LobbyCustomAvatars || !ModSettings.EnableLobbyCustomAvatars)
                continue;

            PollDeferredCacheWrites();
            PollDeferredOutbound();
        }
    }

    public static void RequestLobbyAvatarFile(string md5HexUpper, string ownerUserId)
    {
        if (!MpChatFeatures.LobbyCustomAvatars)
            return;
        if (!CustomAvatarHashUtil.LooksLikeMd5Hex(md5HexUpper))
            return;

        md5HexUpper = md5HexUpper.ToUpperInvariant();
        if (CustomAvatarLobbyHashCache.TryGetPath(md5HexUpper, out _))
            return;

        lock (Gate)
        {
            if (IncomingByHash.ContainsKey(md5HexUpper))
                return;
            if (RequestedHashes.Contains(md5HexUpper))
                return;
        }

        if (!MpChatPerformanceGate.CanRunLobbyAvatarFileTransfer)
        {
            lock (Gate)
                DeferredFileRequestByHash[md5HexUpper] = ownerUserId ?? "";
            return;
        }

        var host = _lobbyScopeTransferManager;
        if (host == null)
        {
            lock (Gate)
                DeferredFileRequestByHash[md5HexUpper] = ownerUserId ?? "";
            return;
        }

        var now = Time.realtimeSinceStartup;
        lock (Gate)
        {
            if (RequestSentAt.TryGetValue(md5HexUpper, out var last) && now - last < RequestCooldownSeconds)
                return;

            RequestSentAt[md5HexUpper] = now;
            RequestedHashes.Add(md5HexUpper);
        }

        host.SendFileRequest(md5HexUpper, ownerUserId);
    }

    // Disabled: proactive .avatar push on connect flooded BeatTogether relays.
    public static void TryProactiveShareLocalAvatar(string targetUserId)
    {
        _ = targetUserId;
    }

    private void SendFileRequest(string hash, string ownerUserId)
    {
        _requestPacket.HashMd5Hex = hash;
        _requestPacket.TargetUserId = string.IsNullOrEmpty(ownerUserId) ? null : ownerUserId;
        try
        {
            _sessionManager.Send(_requestPacket);
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] File request send failed: {ex.Message}");
        }
    }

    public void HandleFileRequest(MpCustomAvatarFileRequestPacket packet, IConnectedPlayer sender)
    {
        if (sender == null || string.IsNullOrEmpty(sender.userId))
            return;
        if (!ModSettings.EnableLobbyCustomAvatars)
            return;

        var hash = (packet.HashMd5Hex ?? "").Trim().ToUpperInvariant();
        if (!CustomAvatarHashUtil.LooksLikeMd5Hex(hash))
            return;

        if (!ShouldRespondToFileRequest(packet.TargetUserId, hash))
            return;

        if (string.IsNullOrEmpty(packet.TargetUserId))
            return;

        if (!CustomAvatarLobbyHashCache.TryGetPath(hash, out var path) || !File.Exists(path))
            return;

        var job = new OutboundJob(path, hash, sender.userId, allowCacheFanOut: false);
        if (!MpChatPerformanceGate.CanRunLobbyAvatarFileTransfer)
        {
            lock (Gate)
                DeferredOutboundJobs.Enqueue(job);
            return;
        }

        EnqueueOutboundJob(job);
    }

    private bool ShouldRespondToFileRequest(string? routedOwnerUserId, string hash)
    {
        var local = _sessionManager.localPlayer;
        if (local == null || string.IsNullOrEmpty(local.userId))
            return false;

        if (!string.IsNullOrEmpty(routedOwnerUserId) &&
            !string.Equals(routedOwnerUserId, local.userId, StringComparison.Ordinal))
            return false;

        var localHash = ModSettings.LobbyCustomAvatarContentHash.Trim().ToUpperInvariant();
        if (CustomAvatarHashUtil.LooksLikeMd5Hex(localHash) &&
            string.Equals(localHash, hash, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private void EnqueueOutbound(string hash, string path, string? targetUserId, bool allowCacheFanOut)
    {
        var job = new OutboundJob(path, hash, targetUserId, allowCacheFanOut);
        if (!MpChatPerformanceGate.CanRunLobbyAvatarFileTransfer ||
            MpChatPerformanceGate.ShouldBlockAvatarHeavyWork)
        {
            lock (Gate)
                DeferredOutboundJobs.Enqueue(job);
            return;
        }

        EnqueueOutboundJob(job);
    }

    private void EnqueueOutboundJob(OutboundJob job)
    {
        var transferKey = BuildTransferKey(job.Hash, job.TargetUserId, job.AllowCacheFanOut);
        lock (Gate)
        {
            if (OutboundTransferKeysInFlight.Contains(transferKey))
                return;
            OutboundTransferKeysInFlight.Add(transferKey);
        }

        _sendQueue.Enqueue(job);
        if (_sendRoutine == null)
            _sendRoutine = StartCoroutine(SendChunksRoutine());
    }

    public void HandleFileChunk(MpCustomAvatarFileChunkPacket packet, IConnectedPlayer sender)
    {
        if (!MpChatPerformanceGate.CanAcceptLobbyAvatarFileChunks)
            return;
        if (sender == null || string.IsNullOrEmpty(sender.userId))
            return;

        var hash = (packet.HashMd5Hex ?? "").Trim().ToUpperInvariant();
        if (!CustomAvatarHashUtil.LooksLikeMd5Hex(hash))
            return;
        if (packet.ChunkCount == 0 || packet.Payload == null || packet.Payload.Length == 0)
            return;
        if (packet.ChunkIndex >= packet.ChunkCount)
            return;

        if (CustomAvatarLobbyHashCache.TryGetPath(hash, out _))
            return;

        IncomingAssembly assembly;
        var startedNewDownload = false;
        lock (Gate)
        {
            if (!IncomingByHash.TryGetValue(hash, out assembly!))
            {
                assembly = new IncomingAssembly(hash, packet.ChunkCount);
                IncomingByHash[hash] = assembly;
                startedNewDownload = true;
            }

            if (assembly.ChunkCount != packet.ChunkCount)
                return;

            if (assembly.Chunks[packet.ChunkIndex] != null)
                return;

            assembly.Chunks[packet.ChunkIndex] = packet.Payload;
        }

        if (startedNewDownload && DownloadNotifiedHashes.Add(hash))
            MpCustomAvatarUserNotifier.PostDownloading(sender.userId, sender.userName);

        if (!assembly.IsComplete())
            return;

        byte[] fileBytes;
        try
        {
            fileBytes = assembly.Build();
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Assemble failed for {hash}: {ex.Message}");
            lock (Gate)
            {
                IncomingByHash.Remove(hash);
                DownloadNotifiedHashes.Remove(hash);
            }

            return;
        }

        lock (Gate)
        {
            IncomingByHash.Remove(hash);
            DownloadNotifiedHashes.Remove(hash);
        }

        if (fileBytes.Length > MpCustomAvatarFileChunkPacket.MaxTotalFileBytes)
            return;

        var computed = CustomAvatarHashUtil.Md5HexBytes(fileBytes);
        if (!string.Equals(computed, hash, StringComparison.OrdinalIgnoreCase))
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Download hash mismatch for {hash} (got {computed})");
            return;
        }

        if (!MpChatPerformanceGate.CanRunLobbyAvatarFileTransfer)
        {
            lock (Gate)
                DeferredCacheWrites.Enqueue(new PendingCacheWrite(fileBytes, hash));
            return;
        }

        FinishCachedRemoteAvatar(fileBytes, hash);
    }

    public static void PollDeferredCacheWrites()
    {
        if (!MpChatPerformanceGate.CanRunLobbyAvatarFileTransfer)
            return;

        PendingCacheWrite[] batch;
        lock (Gate)
        {
            if (DeferredCacheWrites.Count == 0)
                return;
            batch = new PendingCacheWrite[DeferredCacheWrites.Count];
            DeferredCacheWrites.CopyTo(batch, 0);
            DeferredCacheWrites.Clear();
        }

        foreach (var job in batch)
            FinishCachedRemoteAvatar(job.FileBytes, job.Hash);
    }

    private static void FinishCachedRemoteAvatar(byte[] fileBytes, string hash)
    {
        try
        {
            Directory.CreateDirectory(CustomAvatarLobbyCachePaths.CacheDirectory);
            var dest = CustomAvatarLobbyCachePaths.PathForHash(hash);
            File.WriteAllBytes(dest, fileBytes);
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Cache write failed: {ex.Message}");
            return;
        }

        lock (Gate)
            RequestSentAt.Remove(hash);

        CustomAvatarLobbyHashCache.RegisterLobbyCacheFile(hash);
        MultiplayerChat.Plugin.Log?.Info($"[MPChat][LobbyAvatar] Cached remote .avatar {hash} ({fileBytes.Length} bytes)");
        LobbyAvatarFileCached?.Invoke(hash);
        MpCustomAvatarSyncManager.NotifyAllRemotesWithHash(hash);
    }

    public static void PollDeferredOutbound()
    {
        if (!MpChatPerformanceGate.CanRunLobbyAvatarFileTransfer)
            return;

        var host = _lobbyScopeTransferManager;
        if (host == null)
            return;

        OutboundJob[] batch;
        lock (Gate)
        {
            if (DeferredOutboundJobs.Count == 0)
                return;
            batch = new OutboundJob[DeferredOutboundJobs.Count];
            DeferredOutboundJobs.CopyTo(batch, 0);
            DeferredOutboundJobs.Clear();
        }

        for (var i = 0; i < batch.Length; i++)
            host.EnqueueOutboundJob(batch[i]);
    }

    private IEnumerator SendChunksRoutine()
    {
        while (_sendQueue.Count > 0)
        {
            if (!MpChatPerformanceGate.CanRunLobbyAvatarFileTransfer ||
                MpChatPerformanceGate.ShouldThrottleAvatarFileSend)
            {
                while (_sendQueue.Count > 0)
                {
                    var deferred = _sendQueue.Dequeue();
                    lock (Gate)
                        DeferredOutboundJobs.Enqueue(deferred);
                }

                break;
            }

            var job = _sendQueue.Dequeue();
            var transferKey = BuildTransferKey(job.Hash, job.TargetUserId, job.AllowCacheFanOut);
            if (!TryGetOutboundBytes(job, out var bytes))
            {
                lock (Gate)
                    OutboundTransferKeysInFlight.Remove(transferKey);
                continue;
            }

            if (bytes.Length > MpCustomAvatarFileChunkPacket.MaxTotalFileBytes)
            {
                MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Avatar too large to share: {bytes.Length} bytes");
                lock (Gate)
                    OutboundTransferKeysInFlight.Remove(transferKey);
                continue;
            }

            var chunkSize = MpCustomAvatarFileChunkPacket.MaxChunkPayloadBytes;
            var chunkCount = (ushort)((bytes.Length + chunkSize - 1) / chunkSize);
            if (chunkCount == 0)
                chunkCount = 1;

            var useFastUnicast = !job.AllowCacheFanOut && !string.IsNullOrEmpty(job.TargetUserId);
            var completedAllChunks = true;
            var sentThisFrame = 0;

            for (ushort i = 0; i < chunkCount; i++)
            {
                if (!MpChatPerformanceGate.CanRunLobbyAvatarFileTransfer)
                {
                    completedAllChunks = false;
                    break;
                }

                var offset = i * chunkSize;
                var len = Math.Min(chunkSize, bytes.Length - offset);
                if (_chunkScratch.Length < len)
                    _chunkScratch = new byte[len];

                Buffer.BlockCopy(bytes, offset, _chunkScratch, 0, len);

                _chunkPacket.Version = MpCustomAvatarFileChunkPacket.WireVersion;
                _chunkPacket.HashMd5Hex = job.Hash;
                _chunkPacket.TargetUserId = job.AllowCacheFanOut ? null : job.TargetUserId;
                _chunkPacket.ChunkIndex = i;
                _chunkPacket.ChunkCount = chunkCount;
                _chunkPacket.Payload = SliceChunk(len);

                try
                {
                    _sessionManager.Send(_chunkPacket);
                }
                catch (Exception ex)
                {
                    MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Chunk send failed: {ex.Message}");
                    completedAllChunks = false;
                    break;
                }

                if (useFastUnicast)
                {
                    sentThisFrame += len;
                    if (sentThisFrame >= UnicastBytesBudgetPerFrame && i + 1 < chunkCount)
                    {
                        sentThisFrame = 0;
                        yield return null;
                    }
                }
                else if (i + 1 < chunkCount)
                {
                    yield return new WaitForSeconds(MulticastChunkIntervalSeconds);
                }
            }

            if (!completedAllChunks)
            {
                lock (Gate)
                {
                    OutboundTransferKeysInFlight.Remove(transferKey);
                    DeferredOutboundJobs.Enqueue(job);
                }
            }
            else
            {
                if (!useFastUnicast)
                    yield return new WaitForSeconds(MulticastChunkIntervalSeconds);

                lock (Gate)
                    OutboundTransferKeysInFlight.Remove(transferKey);
            }
        }

        _sendRoutine = null;
    }

    private byte[] SliceChunk(int len)
    {
        var slice = new byte[len];
        Buffer.BlockCopy(_chunkScratch, 0, slice, 0, len);
        return slice;
    }

    private static bool TryGetOutboundBytes(OutboundJob job, out byte[] bytes)
    {
        try
        {
            bytes = File.ReadAllBytes(job.Path);
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Read for upload failed: {ex.Message}");
            bytes = Array.Empty<byte>();
            return false;
        }

        return bytes.Length > 0;
    }

    private int GetConnectedPlayerCount()
    {
        try
        {
            return _sessionManager.connectedPlayers?.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string BuildTransferKey(string hash, string? targetUserId, bool allowCacheFanOut) =>
        allowCacheFanOut ? hash + "|*" : hash + "|" + (targetUserId ?? "");

    private readonly struct PendingCacheWrite
    {
        public PendingCacheWrite(byte[] fileBytes, string hash)
        {
            FileBytes = fileBytes;
            Hash = hash;
        }

        public byte[] FileBytes { get; }

        public string Hash { get; }
    }

    private sealed class OutboundJob
    {
        public OutboundJob(string path, string hash, string? targetUserId, bool allowCacheFanOut)
        {
            Path = path;
            Hash = hash;
            TargetUserId = targetUserId;
            AllowCacheFanOut = allowCacheFanOut;
        }

        public string Path { get; }

        public string Hash { get; }

        public string? TargetUserId { get; }

        public bool AllowCacheFanOut { get; }
    }

    private sealed class IncomingAssembly
    {
        public IncomingAssembly(string hash, ushort chunkCount)
        {
            Hash = hash;
            ChunkCount = chunkCount;
            Chunks = new byte[chunkCount][];
        }

        public string Hash { get; }

        public ushort ChunkCount { get; }

        public byte[][] Chunks { get; }

        public bool IsComplete()
        {
            for (var i = 0; i < ChunkCount; i++)
            {
                if (Chunks[i] == null || Chunks[i].Length == 0)
                    return false;
            }

            return true;
        }

        public byte[] Build()
        {
            var total = 0;
            for (var i = 0; i < ChunkCount; i++)
                total += Chunks[i].Length;

            var buf = new byte[total];
            var pos = 0;
            for (var i = 0; i < ChunkCount; i++)
            {
                Buffer.BlockCopy(Chunks[i], 0, buf, pos, Chunks[i].Length);
                pos += Chunks[i].Length;
            }

            return buf;
        }
    }
}
