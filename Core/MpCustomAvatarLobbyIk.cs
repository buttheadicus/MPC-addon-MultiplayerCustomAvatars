using System;
using System.Reflection;
using CustomAvatar.Avatar;
using UnityEngine;

namespace MultiplayerChat.Core;

internal static class MpCustomAvatarLobbyIk
{
    internal static void EnableLocomotion(SpawnedAvatar spawned) => SetLocomotionEnabled(spawned, true);

    internal static void SetLocomotionEnabled(SpawnedAvatar spawned, bool enabled)
    {
        if (spawned == null)
            return;

        try
        {
            var root = spawned.gameObject;
            if (root == null)
                return;

            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null)
                    continue;

                var typeName = mb.GetType().Name;
                if (typeName != "AvatarIK" && typeName != "VRIK")
                    continue;

                var prop = mb.GetType().GetProperty("isLocomotionEnabled",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite)
                    prop.SetValue(mb, enabled, null);

                mb.enabled = enabled;
            }
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn($"[MPChat][LobbyAvatar] SetLocomotionEnabled skipped: {ex.Message}");
        }
    }
}
