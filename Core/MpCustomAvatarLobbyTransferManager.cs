using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MultiplayerChat.Network;
using MultiplayerChat.Settings;
using MultiplayerCore.Models;
using MultiplayerCore.Networking;
using UnityEngine;
using Zenject;

namespace MultiplayerChat.Core;

// Lobby .avatar transfer: on-demand only, paced unicast when requested.
public sealed class MpCustomAvatarLobbyTransferManager : MonoBehaviour, IInitializable
{
    public static MpCustomAvatarLobbyTransferManager? Instance { get; private set; }

    public static event Action<string>? LobbyAvatarFileCached;

    private const float RequestCooldownSeconds = 0.5f;

    // Steady send rate for one-time lobby .avatar transfers (mebibytes per second).
    private const double UnicastBytesPerSecond = 512 * 1024;

    // Cap synchronous chunk work per frame so Unity keeps responding.
    private const int MaxUnicastChunksPerFrame = 1;

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

    [Inject(Optional = true)] private readonly IMultiplayerSessionManager? _sessionManager;

    private Coroutine? _sendRoutine;

    private readonly Queue<OutboundJob> _sendQueue = new();

    private readonly MpCustomAvatarFileRequestPacket _requestPacket = new();

    private readonly MpCustomAvatarFileChunkPacket _chunkPacket = new();

    private byte[] _chunkScratch = Array.Empty<byte>();

    private Coroutine? _gradualFlushRoutine;

    private bool _started;

    public void Initialize() => EnsureInitialized();

    public void EnsureInitialized()
    {
        if (_started)
            return;

        _started = true;
        _lobbyScopeTransferManager = this;
        Instance = this;
    }

    private IMultiplayerSessionManager? ResolveSessionManager() =>
        MpChatAddonSessionResolver.Resolve(_sessionManager);

    private void OnDestroy()
    {
        if (_gradualFlushRoutine != null)
        {
            StopCoroutine(_gradualFlushRoutine);
            _gradualFlushRoutine = null;
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

    private static bool CanRunLobbyAvatarFileWork() => MpChatAvatarWorkloadGate.CanRunLobbyAvatarFileWork;

    // Spread deferred lobby downloads across many frames instead of one burst on arena return.
    public static void ScheduleGradualLobbyReturnFlush()
    {
        var host = _lobbyScopeTransferManager;
        if (host == null)
            return;

        if (host._gradualFlushRoutine != null)
            return;

        host._gradualFlushRoutine = host.StartCoroutine(host.GradualLobbyReturnFlushRoutine());
    }

    public static void FlushDeferredLobbyAvatarFileTransfers(bool rescanMissingRemotes = false)
    {
        if (rescanMissingRemotes)
        {
            ScheduleGradualLobbyReturnFlush();
            return;
        }

        if (!CanRunLobbyAvatarFileWork())
            return;

        DrainOneDeferredFileRequest();
        PollDeferredCacheWrites(maxWrites: 1);
        PollDeferredOutbound(maxJobs: 1);
    }

    private static void DrainOneDeferredFileRequest()
    {
        if (!CanRunLobbyAvatarFileWork())
            return;

        KeyValuePair<string, string> request = default;
        var found = false;
        lock (Gate)
        {
            foreach (var kvp in DeferredFileRequestByHash)
            {
                request = kvp;
                DeferredFileRequestByHash.Remove(kvp.Key);
                found = true;
                break;
            }
        }

        if (found)
            RequestLobbyAvatarFile(request.Key, request.Value);
    }

    private static bool HasDeferredTransferWork()
    {
        lock (Gate)
        {
            return DeferredFileRequestByHash.Count > 0 ||
                   DeferredCacheWrites.Count > 0 ||
                   DeferredOutboundJobs.Count > 0;
        }
    }

    private IEnumerator GradualLobbyReturnFlushRoutine()
    {
        yield return new WaitForSecondsRealtime(1.25f);

        while (HasDeferredTransferWork())
        {
            if (!CanRunLobbyAvatarFileWork())
            {
                yield return new WaitForSeconds(0.35f);
                continue;
            }

            DrainOneDeferredFileRequest();
            PollDeferredCacheWrites(maxWrites: 1);
            PollDeferredOutbound(maxJobs: 1);
            yield return new WaitForSeconds(0.45f);
        }

        if (CanRunLobbyAvatarFileWork())
            MpCustomAvatarSyncManager.RequestMissingRemoteAvatarFiles();

        _gradualFlushRoutine = null;
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

        if (!CanRunLobbyAvatarFileWork())
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
        var session = ResolveSessionManager();
        if (session == null)
            return;

        _requestPacket.HashMd5Hex = hash;
        _requestPacket.TargetUserId = string.IsNullOrEmpty(ownerUserId) ? null : ownerUserId;
        try
        {
            session.Send(_requestPacket);
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
        if (!CanRunLobbyAvatarFileWork())
        {
            lock (Gate)
                DeferredOutboundJobs.Enqueue(job);
            return;
        }

        EnqueueOutboundJob(job);
    }

    private bool ShouldRespondToFileRequest(string? routedOwnerUserId, string hash)
    {
        var local = ResolveSessionManager()?.localPlayer;
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
        if (!CanRunLobbyAvatarFileWork() ||
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

        IncomingAssembly completed;
        lock (Gate)
        {
            if (!IncomingByHash.TryGetValue(hash, out completed!))
                return;

            IncomingByHash.Remove(hash);
            DownloadNotifiedHashes.Remove(hash);
        }

        var host = _lobbyScopeTransferManager ?? Instance;
        if (host != null)
            host.StartCoroutine(host.CompleteIncomingDownloadRoutine(completed));
    }

    private IEnumerator CompleteIncomingDownloadRoutine(IncomingAssembly assembly)
    {
        yield return null;

        Task<(byte[] FileBytes, string ComputedHash)>? buildTask = null;
        try
        {
            buildTask = Task.Run(() =>
            {
                var fileBytes = assembly.Build();
                if (fileBytes.Length > MpCustomAvatarFileChunkPacket.MaxTotalFileBytes)
                    throw new InvalidOperationException("Avatar file exceeds size limit");

                var computed = CustomAvatarHashUtil.Md5HexBytes(fileBytes);
                return (fileBytes, computed);
            });
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn(
                $"[MPChat][LobbyAvatar] Assemble task failed for {assembly.Hash}: {ex.Message}");
            yield break;
        }

        while (buildTask != null && !buildTask.IsCompleted)
            yield return null;

        if (buildTask == null)
            yield break;

        if (buildTask.IsFaulted)
        {
            MultiplayerChat.Plugin.Log?.Warn(
                $"[MPChat][LobbyAvatar] Assemble failed for {assembly.Hash}: {buildTask.Exception?.GetBaseException().Message}");
            yield break;
        }

        var (fileBytes, computed) = buildTask.Result;

        if (!string.Equals(computed, assembly.Hash, StringComparison.OrdinalIgnoreCase))
        {
            MultiplayerChat.Plugin.Log?.Warn(
                $"[MPChat][LobbyAvatar] Download hash mismatch for {assembly.Hash} (got {computed})");
            yield break;
        }

        if (!CanRunLobbyAvatarFileWork())
        {
            lock (Gate)
                DeferredCacheWrites.Enqueue(new PendingCacheWrite(fileBytes, assembly.Hash));
            yield break;
        }

        yield return FinishCachedRemoteAvatarRoutine(fileBytes, assembly.Hash);
    }

    public static void PollDeferredCacheWrites(int maxWrites = int.MaxValue)
    {
        if (!CanRunLobbyAvatarFileWork())
            return;

        if (maxWrites <= 0)
            return;

        for (var i = 0; i < maxWrites; i++)
        {
            PendingCacheWrite job;
            lock (Gate)
            {
                if (DeferredCacheWrites.Count == 0)
                    return;

                job = DeferredCacheWrites.Dequeue();
            }

            var host = _lobbyScopeTransferManager;
            if (host != null)
                host.StartCoroutine(host.FinishCachedRemoteAvatarRoutine(job.FileBytes, job.Hash));
            else
                FinishCachedRemoteAvatarImmediate(job.FileBytes, job.Hash);
        }
    }

    private IEnumerator FinishCachedRemoteAvatarRoutine(byte[] fileBytes, string hash)
    {
        yield return null;

        Exception? writeError = null;
        var writeTask = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(CustomAvatarLobbyCachePaths.CacheDirectory);
                var dest = CustomAvatarLobbyCachePaths.PathForHash(hash);
                File.WriteAllBytes(dest, fileBytes);
            }
            catch (Exception ex)
            {
                writeError = ex;
            }
        });

        while (!writeTask.IsCompleted)
            yield return null;

        if (writeError != null)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Cache write failed: {writeError.Message}");
            yield break;
        }

        FinishCachedRemoteAvatarAfterWrite(hash, fileBytes.Length);
    }

    private static void FinishCachedRemoteAvatar(byte[] fileBytes, string hash)
    {
        var host = _lobbyScopeTransferManager;
        if (host != null)
            host.StartCoroutine(host.FinishCachedRemoteAvatarRoutine(fileBytes, hash));
        else
            FinishCachedRemoteAvatarImmediate(fileBytes, hash);
    }

    private static void FinishCachedRemoteAvatarImmediate(byte[] fileBytes, string hash)
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

        FinishCachedRemoteAvatarAfterWrite(hash, fileBytes.Length);
    }

    private static void FinishCachedRemoteAvatarAfterWrite(string hash, int byteLength)
    {
        lock (Gate)
            RequestSentAt.Remove(hash);

        CustomAvatarLobbyHashCache.RegisterLobbyCacheFile(hash);
        MultiplayerChat.Plugin.Log?.Info($"[MPChat][LobbyAvatar] Cached remote .avatar {hash} ({byteLength} bytes)");
        LobbyAvatarFileCached?.Invoke(hash);
        MpCustomAvatarSyncManager.NotifyAllRemotesWithHash(hash);
    }

    public static void PollDeferredOutbound(int maxJobs = int.MaxValue)
    {
        if (!CanRunLobbyAvatarFileWork())
            return;

        var host = _lobbyScopeTransferManager;
        if (host == null)
            return;

        if (maxJobs <= 0)
            return;

        for (var i = 0; i < maxJobs; i++)
        {
            OutboundJob job;
            lock (Gate)
            {
                if (DeferredOutboundJobs.Count == 0)
                    return;

                job = DeferredOutboundJobs.Dequeue();
            }

            host.EnqueueOutboundJob(job);
        }
    }

    private IEnumerator SendChunksRoutine()
    {
        while (_sendQueue.Count > 0)
        {
            if (!CanRunLobbyAvatarFileWork() ||
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
            var session = ResolveSessionManager();
            if (session == null)
            {
                lock (Gate)
                    DeferredOutboundJobs.Enqueue(job);
                break;
            }

            var transferKey = BuildTransferKey(job.Hash, job.TargetUserId, job.AllowCacheFanOut);
            yield return null;

            if (!TryOpenOutboundFile(job, out var fileStream, out var fileLength))
            {
                lock (Gate)
                    OutboundTransferKeysInFlight.Remove(transferKey);
                continue;
            }

            using (fileStream)
            {
                if (fileLength > MpCustomAvatarFileChunkPacket.MaxTotalFileBytes)
                {
                    MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Avatar too large to share: {fileLength} bytes");
                    lock (Gate)
                        OutboundTransferKeysInFlight.Remove(transferKey);
                    continue;
                }

                var chunkSize = MpCustomAvatarFileChunkPacket.MaxChunkPayloadBytes;
                var chunkCount = (ushort)((fileLength + chunkSize - 1) / chunkSize);
                if (chunkCount == 0)
                    chunkCount = 1;

                var useFastUnicast = !job.AllowCacheFanOut && !string.IsNullOrEmpty(job.TargetUserId);
                var completedAllChunks = true;
                var chunksThisFrame = 0;
                var byteCredit = 0d;
                var lastCreditRealtime = (double)Time.realtimeSinceStartup;

                for (ushort i = 0; i < chunkCount; i++)
                {
                    if (!CanRunLobbyAvatarFileWork())
                    {
                        completedAllChunks = false;
                        break;
                    }

                    var offset = (long)i * chunkSize;
                    var len = (int)Math.Min(chunkSize, fileLength - offset);

                    if (useFastUnicast)
                    {
                        while (byteCredit + 1e-6 < len)
                        {
                            yield return null;
                            var now = (double)Time.realtimeSinceStartup;
                            byteCredit += (now - lastCreditRealtime) * UnicastBytesPerSecond;
                            lastCreditRealtime = now;
                        }

                        byteCredit -= len;
                    }

                    if (_chunkScratch.Length < len)
                        _chunkScratch = new byte[len];

                    fileStream.Seek(offset, SeekOrigin.Begin);
                    var read = 0;
                    while (read < len)
                    {
                        var n = fileStream.Read(_chunkScratch, read, len - read);
                        if (n <= 0)
                            break;

                        read += n;
                    }

                    if (read != len)
                    {
                        MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Read short chunk for {job.Hash}");
                        completedAllChunks = false;
                        break;
                    }

                    _chunkPacket.Version = MpCustomAvatarFileChunkPacket.WireVersion;
                    _chunkPacket.HashMd5Hex = job.Hash;
                    _chunkPacket.TargetUserId = job.AllowCacheFanOut ? null : job.TargetUserId;
                    _chunkPacket.ChunkIndex = i;
                    _chunkPacket.ChunkCount = chunkCount;
                    _chunkPacket.Payload = SliceChunk(len);

                    try
                    {
                        session.Send(_chunkPacket);
                    }
                    catch (Exception ex)
                    {
                        MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Chunk send failed: {ex.Message}");
                        completedAllChunks = false;
                        break;
                    }

                    if (useFastUnicast)
                    {
                        chunksThisFrame++;
                        if (chunksThisFrame >= MaxUnicastChunksPerFrame && i + 1 < chunkCount)
                        {
                            chunksThisFrame = 0;
                            yield return null;
                        }
                    }
                    else if (i + 1 < chunkCount)
                    {
                        yield return new WaitForSecondsRealtime((float)(len / UnicastBytesPerSecond));
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
                        yield return new WaitForSecondsRealtime((float)(chunkSize / UnicastBytesPerSecond));

                    lock (Gate)
                        OutboundTransferKeysInFlight.Remove(transferKey);
                }
            }
        }

        _sendRoutine = null;
    }

    private static bool TryOpenOutboundFile(OutboundJob job, out FileStream stream, out long fileLength)
    {
        stream = null!;
        fileLength = 0;
        try
        {
            var info = new FileInfo(job.Path);
            if (!info.Exists || info.Length <= 0)
                return false;

            fileLength = info.Length;
            stream = File.Open(job.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Read for upload failed: {ex.Message}");
            stream = null!;
            return false;
        }
    }

    private byte[] SliceChunk(int len)
    {
        var slice = new byte[len];
        Buffer.BlockCopy(_chunkScratch, 0, slice, 0, len);
        return slice;
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
