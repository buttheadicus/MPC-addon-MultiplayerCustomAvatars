using System;
using UnityEngine;

namespace MultiplayerChat.Core;

public readonly struct MpCustomAvatarFbtPose : IEquatable<MpCustomAvatarFbtPose>
{
    public readonly Vector3 PelvisPosition;

    public readonly Quaternion PelvisRotation;

    public MpCustomAvatarFbtPose(Vector3 pelvisPosition, Quaternion pelvisRotation)
    {
        PelvisPosition = pelvisPosition;
        PelvisRotation = pelvisRotation;
    }

    public bool Equals(MpCustomAvatarFbtPose other) =>
        PelvisPosition.Equals(other.PelvisPosition) && PelvisRotation.Equals(other.PelvisRotation);

    public override bool Equals(object? obj) => obj is MpCustomAvatarFbtPose other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return (PelvisPosition.GetHashCode() * 397) ^ PelvisRotation.GetHashCode();
        }
    }
}

public static class MpCustomAvatarFbtBlob
{
    public const byte InnerVersion = 1;

    public const int V1ByteLength = 1 + 12 + 16;

    public static byte[] EncodeV1(in MpCustomAvatarFbtPose pose)
    {
        var b = new byte[V1ByteLength];
        b[0] = InnerVersion;
        WriteFloat(b, 1, pose.PelvisPosition.x);
        WriteFloat(b, 5, pose.PelvisPosition.y);
        WriteFloat(b, 9, pose.PelvisPosition.z);
        WriteFloat(b, 13, pose.PelvisRotation.x);
        WriteFloat(b, 17, pose.PelvisRotation.y);
        WriteFloat(b, 21, pose.PelvisRotation.z);
        WriteFloat(b, 25, pose.PelvisRotation.w);
        return b;
    }

    public static bool TryDecode(byte[]? blob, out MpCustomAvatarFbtPose pose)
    {
        pose = default;
        if (blob == null || blob.Length < V1ByteLength || blob[0] != InnerVersion)
            return false;

        pose = new MpCustomAvatarFbtPose(
            new Vector3(ReadFloat(blob, 1), ReadFloat(blob, 5), ReadFloat(blob, 9)),
            new Quaternion(ReadFloat(blob, 13), ReadFloat(blob, 17), ReadFloat(blob, 21), ReadFloat(blob, 25)));
        return true;
    }

    private static void WriteFloat(byte[] b, int offset, float v)
    {
        var tmp = BitConverter.GetBytes(v);
        Buffer.BlockCopy(tmp, 0, b, offset, 4);
    }

    private static float ReadFloat(byte[] b, int offset) => BitConverter.ToSingle(b, offset);
}

public static class MpCustomAvatarLocalPoseSource
{
    public static bool PelvisTrackingEnabled { get; set; }

    public static bool TryGetPelvisPose(out MpCustomAvatarFbtPose pose)
    {
        pose = default;
        return false;
    }
}
