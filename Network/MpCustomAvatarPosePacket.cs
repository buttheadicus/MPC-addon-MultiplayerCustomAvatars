using System;
using LiteNetLib.Utils;

namespace MultiplayerChat.Network;

public class MpCustomAvatarPosePacket : MultiplayerCore.Networking.Abstractions.MpPacket
{
    public const byte WireVersion = 2;

    public const byte FlagHasDescriptor = 1;

    public const byte FlagHasFbtBlob = 2;

    public const byte FlagHasScale = 4;

    public const int MaxDescriptorChars = 160;

    public const int MaxFbtBlobBytes = 512;

    public byte Flags;

    public string? AvatarDescriptorId;

    public float AvatarScale = 1f;

    public byte[]? FbtBlob;

    // When set, only the targeted peer should apply this update for the sender.
    public string? TargetUserId;

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(WireVersion);
        writer.Put(Flags);
        if ((Flags & FlagHasDescriptor) != 0)
            writer.Put(TruncateDescriptor(AvatarDescriptorId));

        if ((Flags & FlagHasScale) != 0)
            writer.Put(AvatarScale);

        if ((Flags & FlagHasFbtBlob) != 0)
        {
            var blob = FbtBlob ?? Array.Empty<byte>();
            if (blob.Length > MaxFbtBlobBytes)
            {
                var trimmed = new byte[MaxFbtBlobBytes];
                Buffer.BlockCopy(blob, 0, trimmed, 0, MaxFbtBlobBytes);
                blob = trimmed;
            }

            writer.PutBytesWithLength(blob);
        }

        writer.Put(TargetUserId ?? "");
    }

    public override void Deserialize(NetDataReader reader)
    {
        Flags = 0;
        AvatarDescriptorId = null;
        AvatarScale = 1f;
        FbtBlob = null;
        TargetUserId = null;

        if (reader.AvailableBytes <= 0)
            return;

        var ver = reader.GetByte();
        if (ver != 1 && ver != WireVersion)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] Unsupported MpCustomAvatarPosePacket wire version {ver}");
            return;
        }

        if (reader.AvailableBytes <= 0)
            return;

        Flags = reader.GetByte();

        if ((Flags & FlagHasDescriptor) != 0 && reader.AvailableBytes > 0)
        {
            var d = reader.GetString();
            if (!string.IsNullOrEmpty(d))
                AvatarDescriptorId = d.Length > MaxDescriptorChars ? d.Substring(0, MaxDescriptorChars) : d;
        }

        if ((Flags & FlagHasScale) != 0 && reader.AvailableBytes > 0)
            AvatarScale = reader.GetFloat();

        if ((Flags & FlagHasFbtBlob) != 0 && reader.AvailableBytes > 0)
        {
            try
            {
                var blob = reader.GetBytesWithLength();
                if (blob != null && blob.Length > 0)
                {
                    if (blob.Length > MaxFbtBlobBytes)
                    {
                        var trimmed = new byte[MaxFbtBlobBytes];
                        Buffer.BlockCopy(blob, 0, trimmed, 0, MaxFbtBlobBytes);
                        FbtBlob = trimmed;
                    }
                    else
                        FbtBlob = blob;
                }
            }
            catch (Exception ex)
            {
                MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] FBT blob deserialize failed: {ex.Message}");
                FbtBlob = null;
            }
        }

        if (ver >= WireVersion && reader.AvailableBytes > 0)
        {
            var target = reader.GetString();
            TargetUserId = string.IsNullOrEmpty(target) ? null : target;
        }
    }

    private static string TruncateDescriptor(string? id)
    {
        var s = id ?? "";
        if (s.Length == 0)
            return "";
        return s.Length <= MaxDescriptorChars ? s : s.Substring(0, MaxDescriptorChars);
    }
}
