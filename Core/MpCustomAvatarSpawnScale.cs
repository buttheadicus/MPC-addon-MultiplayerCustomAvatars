using System.Reflection;
using CustomAvatar.Avatar;
using UnityEngine;

namespace MultiplayerChat.Core;

internal static class MpCustomAvatarSpawnScale
{
    private static PropertyInfo? _scaleProperty;

    internal static void Apply(SpawnedAvatar spawned, float scale)
    {
        if (spawned == null)
            return;

        var go = spawned.gameObject;
        if (go == null)
            return;

        if (TrySetScaleProperty(spawned, scale))
            return;

        go.transform.localScale = Vector3.one * scale;
    }

    private static bool TrySetScaleProperty(SpawnedAvatar spawned, float scale)
    {
        _scaleProperty ??= typeof(SpawnedAvatar).GetProperty("scale",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (_scaleProperty == null)
            _scaleProperty = typeof(SpawnedAvatar).GetProperty("Scale",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (_scaleProperty == null || !_scaleProperty.CanWrite)
            return false;

        try
        {
            _scaleProperty.SetValue(spawned, scale, null);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
