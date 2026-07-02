using System;
using LiteNetLib.Utils;

namespace MultiplayerChat.Network;

public class MpCustomAvatarFileChunkPacket : MultiplayerCore.Networking.Abstractions.MpPacket
{
    public const byte WireVersion = 2;

    public const byte LegacyWireVersion = 1;

    public const int MaxChunkPayloadBytes = 32768;

    public const int MaxTotalFileBytes = 15 * 1024 * 1024;

    public byte Version = WireVersion;

    public string HashMd5Hex = "";

    // Unicast to the requester; empty broadcasts (legacy v1).
    public string? TargetUserId;

    public ushort ChunkIndex;

    public ushort ChunkCount;

    public byte[]? Payload;

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(Version);
        writer.Put(HashMd5Hex ?? "");
        if (Version >= WireVersion)
            writer.Put(TargetUserId ?? "");
        writer.Put(ChunkIndex);
        writer.Put(ChunkCount);
        var data = Payload ?? Array.Empty<byte>();
        if (data.Length > MaxChunkPayloadBytes)
        {
            var trimmed = new byte[MaxChunkPayloadBytes];
            Buffer.BlockCopy(data, 0, trimmed, 0, MaxChunkPayloadBytes);
            data = trimmed;
        }

        writer.PutBytesWithLength(data);
    }

    public override void Deserialize(NetDataReader reader)
    {
        Version = WireVersion;
        HashMd5Hex = "";
        TargetUserId = null;
        ChunkIndex = 0;
        ChunkCount = 0;
        Payload = null;

        if (reader.AvailableBytes <= 0)
            return;

        Version = reader.GetByte();
        if (Version != WireVersion && Version != LegacyWireVersion)
            return;

        HashMd5Hex = reader.GetString() ?? "";
        if (HashMd5Hex.Length > MpCustomAvatarFileRequestPacket.HashLength)
            HashMd5Hex = HashMd5Hex.Substring(0, MpCustomAvatarFileRequestPacket.HashLength);

        if (Version >= WireVersion && reader.AvailableBytes > 0)
        {
            var target = reader.GetString();
            TargetUserId = string.IsNullOrEmpty(target) ? null : target;
        }

        if (reader.AvailableBytes <= 0)
            return;

        ChunkIndex = reader.GetUShort();
        ChunkCount = reader.GetUShort();
        if (reader.AvailableBytes <= 0)
            return;

        try
        {
            var blob = reader.GetBytesWithLength();
            if (blob != null && blob.Length > 0)
            {
                if (blob.Length > MaxChunkPayloadBytes)
                {
                    var trimmed = new byte[MaxChunkPayloadBytes];
                    Buffer.BlockCopy(blob, 0, trimmed, 0, MaxChunkPayloadBytes);
                    Payload = trimmed;
                }
                else
                    Payload = blob;
            }
        }
        catch
        {
            Payload = null;
        }
    }
}
