using UnityEngine;
using Zenject;

namespace MultiplayerChat.Core;

internal static class MpChatArenaFacadeRoots
{
    internal static Transform? FindFrom(Transform? from)
    {
        if (from == null)
            return null;

        var local = from.GetComponentInParent<MultiplayerLocalActivePlayerFacade>();
        if (local != null)
            return local.transform;

        var remote = from.GetComponentInParent<MultiplayerConnectedPlayerFacade>();
        return remote != null ? remote.transform : null;
    }

    internal static bool HasArenaFacade(Transform? from) => FindFrom(from) != null;

    internal static GameObjectContext? FindPlayerContext(Transform? from)
    {
        var root = FindFrom(from);
        return root != null ? root.GetComponent<GameObjectContext>() : null;
    }

    internal static bool IsRedecoratorShadow(Transform? facadeRoot)
    {
        if (facadeRoot == null)
            return true;

        var root = facadeRoot.root;
        return root != null &&
               string.Equals(root.name, "InternalRedecorator", System.StringComparison.Ordinal);
    }
}
