using LiteNetLib.Utils;

namespace MultiplayerChat.Network;

public class MpCustomAvatarFileRequestPacket : MultiplayerCore.Networking.Abstractions.MpPacket
{
    public const int HashLength = 32;

    public string HashMd5Hex = "";

    // Route the request to the avatar owner; empty broadcasts (legacy).
    public string? TargetUserId;

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(HashMd5Hex ?? "");
        writer.Put(TargetUserId ?? "");
    }

    public override void Deserialize(NetDataReader reader)
    {
        HashMd5Hex = reader.GetString() ?? "";
        if (HashMd5Hex.Length > HashLength)
            HashMd5Hex = HashMd5Hex.Substring(0, HashLength);

        TargetUserId = null;
        if (reader.AvailableBytes <= 0)
            return;

        var target = reader.GetString();
        TargetUserId = string.IsNullOrEmpty(target) ? null : target;
    }
}
