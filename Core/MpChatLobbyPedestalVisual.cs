using System;
using BeatSaber.AvatarCore;
using BeatSaber.BeatAvatarSDK;
using UnityEngine;

namespace MultiplayerChat.Core;

// Vanilla lobby pedestals use ScaleAnimator (patched to ~0.05). Keep pedestal full scale while custom is shown.
internal static class MpChatLobbyPedestalVisual
{
    internal static void ResetPedestalScale(Transform pedestalRoot)
    {
        if (pedestalRoot == null)
            return;
        pedestalRoot.localScale = Vector3.one;
    }

    internal static void SetVanillaScaleAnimatorEnabled(Transform pedestalRoot, bool enabled)
    {
        if (pedestalRoot == null)
            return;

        foreach (var mb in pedestalRoot.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb != null && mb.GetType().Name == "ScaleAnimator")
                mb.enabled = enabled;
        }
    }

    internal static void ShowVanillaRig(MpChatLobbyLivePoseInput? avatarInput, Transform pedestalRoot)
    {
        SetVanillaScaleAnimatorEnabled(pedestalRoot, true);
        ResetPedestalScale(pedestalRoot);
        SetNonCustomPedestalRenderersVisible(pedestalRoot, true);
        SetBeatAvatarVisualControllersEnabled(pedestalRoot, true);
        SetAvatarCoreAvatarsEnabled(pedestalRoot, true);
        avatarInput?.SetVanillaRenderersVisible(true);
        avatarInput?.SetEnabled(false);
    }

    internal static void PrepareForCustomAvatar(Transform pedestalRoot)
    {
        SetVanillaScaleAnimatorEnabled(pedestalRoot, false);
        ResetPedestalScale(pedestalRoot);
    }

    internal static void ApplyCustomAvatarVisibility(Transform pedestalRoot, MpChatLobbyLivePoseInput? avatarInput)
    {
        avatarInput?.SetEnabled(true);
        avatarInput?.SetVanillaRenderersVisible(false);
        SetNonCustomPedestalRenderersVisible(pedestalRoot, false);
    }

    // Arena rigs toggle active objects during intro/outro; do not deactivate body hierarchies (custom may parent there).
    internal static void ApplyArenaCustomAvatarVisibility(
        Transform poseRoot,
        Transform facadeRoot,
        MpChatLobbyLivePoseInput? avatarInput,
        Transform? customSpawnRoot)
    {
        avatarInput?.SetVanillaRenderersVisible(false);
        SetVanillaBeatSaberMeshRenderersVisible(poseRoot, false);
        SuppressVanillaArenaRig(facadeRoot, customSpawnRoot);
    }

    internal static void ReapplyArenaSpawnedVisibility(
        Transform poseRoot,
        Transform facadeRoot,
        GameObject spawnedRoot,
        float uniformScale)
    {
        EnsureSpawnedVisible(spawnedRoot, uniformScale);
        SetVanillaBeatSaberMeshRenderersVisible(poseRoot, false);
        SuppressVanillaArenaRig(facadeRoot, spawnedRoot.transform);
    }

    // AvatarCore re-enables itself during intro/gameplay transitions; keep vanilla meshes off while custom is active.
    internal static void SuppressVanillaArenaRig(Transform facadeRoot, Transform? customSpawnRoot)
    {
        if (facadeRoot == null)
            return;

        SetBeatAvatarVisualControllersEnabled(facadeRoot, false);
        SetAvatarCoreAvatarsEnabled(facadeRoot, false);

        foreach (var avatar in facadeRoot.GetComponentsInChildren<Avatar>(true))
        {
            if (avatar == null)
                continue;

            avatar.enabled = false;

            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || IsUnderCustomSpawn(renderer.transform, customSpawnRoot))
                    continue;
                if (ShouldSkipPedestalRenderer(renderer.transform))
                    continue;
                renderer.enabled = false;
            }
        }
    }

    // Cheap check before full hierarchy walks during periodic arena maintain.
    internal static bool ArenaVanillaRigMayNeedSuppress(Transform facadeRoot, Transform? customSpawnRoot)
    {
        if (facadeRoot == null)
            return false;

        foreach (var visual in facadeRoot.GetComponentsInChildren<BeatAvatarVisualController>(true))
        {
            if (visual != null && visual.enabled)
                return true;
        }

        foreach (var avatar in facadeRoot.GetComponentsInChildren<Avatar>(true))
        {
            if (avatar == null || !avatar.enabled)
                continue;

            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled)
                    continue;
                if (IsUnderCustomSpawn(renderer.transform, customSpawnRoot))
                    continue;
                if (ShouldSkipPedestalRenderer(renderer.transform))
                    continue;
                return true;
            }
        }

        return false;
    }

    private static bool IsUnderCustomSpawn(Transform rendererTransform, Transform? customSpawnRoot)
    {
        if (customSpawnRoot == null)
        {
            var r = rendererTransform.GetComponent<Renderer>();
            return r != null && IsCustomAvatarRenderer(r);
        }

        for (var cur = rendererTransform; cur != null; cur = cur.parent)
        {
            if (cur == customSpawnRoot)
                return true;
        }

        return false;
    }

    internal static void SetBeatAvatarVisualControllersEnabled(Transform searchRoot, bool enabled)
    {
        if (searchRoot == null)
            return;

        foreach (var visual in searchRoot.GetComponentsInChildren<BeatAvatarVisualController>(true))
        {
            if (visual != null)
                visual.enabled = enabled;
        }
    }

    // Duel/platform avatars use BeatSaber.AvatarCore.Avatar, not BeatAvatarVisualController on the pose.
    internal static void SetAvatarCoreAvatarsEnabled(Transform searchRoot, bool enabled)
    {
        if (searchRoot == null)
            return;

        foreach (var avatar in searchRoot.GetComponentsInChildren<Avatar>(true))
        {
            if (avatar != null)
                avatar.enabled = enabled;
        }
    }

    private static void SetVanillaBeatSaberMeshRenderersVisible(Transform poseRoot, bool visible)
    {
        if (poseRoot == null)
            return;

        foreach (var renderer in poseRoot.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || ShouldSkipPedestalRenderer(renderer.transform))
                continue;
            if (IsCustomAvatarRenderer(renderer))
                continue;

            renderer.enabled = visible;
        }
    }

    internal static void EnsureSpawnedVisible(GameObject? spawnedRoot, float uniformScale)
    {
        if (spawnedRoot == null)
            return;

        spawnedRoot.SetActive(true);
        spawnedRoot.transform.localScale = Vector3.one * uniformScale;

        foreach (var renderer in spawnedRoot.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer != null)
                renderer.enabled = true;
        }
    }

    internal static void SetNonCustomPedestalRenderersVisible(Transform pedestalRoot, bool visible)
    {
        if (pedestalRoot == null)
            return;

        foreach (var renderer in pedestalRoot.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || ShouldSkipPedestalRenderer(renderer.transform))
                continue;

            if (!visible && IsCustomAvatarRenderer(renderer))
                continue;

            renderer.enabled = visible;
        }
    }

    private static bool ShouldSkipPedestalRenderer(Transform t)
    {
        for (var cur = t; cur != null; cur = cur.parent)
        {
            if (cur.name == "AvatarCaption")
                return true;
            if (cur.name.StartsWith("MpChatLobbyPoseProxy", StringComparison.Ordinal))
                return true;
            if (IsSaberHierarchy(cur))
                return true;
        }

        return false;
    }

    private static bool IsSaberHierarchy(Transform t)
    {
        for (var cur = t; cur != null; cur = cur.parent)
        {
            var n = cur.name;
            if (n.IndexOf("Saber", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (n.IndexOf("Sabers", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    internal static bool IsCustomAvatarRenderer(Renderer renderer)
    {
        foreach (var mb in renderer.GetComponentsInParent<MonoBehaviour>(true))
        {
            if (mb == null)
                continue;

            var ns = mb.GetType().Namespace;
            if (ns != null && ns.StartsWith("CustomAvatar", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
