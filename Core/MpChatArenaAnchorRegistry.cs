using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerChat.Core;

// Avoid FindObjectsOfType<Transform> when cleaning arena avatar anchors.
internal static class MpChatArenaAnchorRegistry
{
    private static readonly List<Transform> Anchors = new(8);

    internal static void Register(Transform anchor)
    {
        if (anchor == null || Anchors.Contains(anchor))
            return;

        Anchors.Add(anchor);
    }

    internal static void Unregister(Transform? anchor)
    {
        if (anchor == null)
            return;

        Anchors.Remove(anchor);
    }

    internal static void DestroyAll()
    {
        for (var i = Anchors.Count - 1; i >= 0; i--)
        {
            var anchor = Anchors[i];
            if (anchor != null)
                Object.Destroy(anchor.gameObject);
            Anchors.RemoveAt(i);
        }
    }
}
