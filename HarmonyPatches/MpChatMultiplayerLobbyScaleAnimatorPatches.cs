using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MultiplayerChat.Core;
using Tweening;
using UnityEngine;

namespace MultiplayerChat.HarmonyPatches;

internal static class MpChatMultiplayerLobbyScaleAnimatorPatches
{
    private const string HarmonyOwnerPrefix = "com.multiplayerchat.addon.customAvatars";

    internal static void Apply(Harmony harmony)
    {
        ClearPriorScaleAnimatorPatches(harmony);
        harmony.CreateClassProcessor(typeof(ScaleAnimatorPatchInitIfNeeded)).Patch();
        harmony.CreateClassProcessor(typeof(ScaleAnimatorPatchHideInstant)).Patch();
    }

    private static void ClearPriorScaleAnimatorPatches(Harmony harmony)
    {
        UnpatchScaleAnimatorMethod(harmony, AccessTools.Method(typeof(global::ScaleAnimator), "InitIfNeeded"));
        UnpatchScaleAnimatorMethod(harmony, AccessTools.Method(typeof(global::ScaleAnimator), "HideInstant"));
    }

    private static void UnpatchScaleAnimatorMethod(Harmony harmony, MethodBase? method)
    {
        if (method == null)
            return;

        var orphanedOwners = new HashSet<string>(StringComparer.Ordinal);
        var patchInfo = Harmony.GetPatchInfo(method);
        if (patchInfo?.Transpilers != null)
        {
            foreach (var patch in patchInfo.Transpilers)
            {
                if (patch.owner != null &&
                    patch.owner.StartsWith(HarmonyOwnerPrefix, StringComparison.Ordinal))
                {
                    orphanedOwners.Add(patch.owner);
                }
            }
        }

        foreach (var owner in orphanedOwners)
            new Harmony(owner).UnpatchSelf();

        harmony.Unpatch(method, HarmonyPatchType.Transpiler);
    }

    [HarmonyPatch]
    internal static class ScaleAnimatorPatchInitIfNeeded
    {
        private static MethodBase? TargetMethod() =>
            AccessTools.Method(typeof(global::ScaleAnimator), "InitIfNeeded");

        private static readonly FieldInfo ScaleUpTweenField =
            typeof(global::ScaleAnimator).GetField("_scaleUpTween", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static readonly FieldInfo ScaleDownTweenField =
            typeof(global::ScaleAnimator).GetField("_scaleDownTween", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static readonly MethodInfo ConstructScaleUpTween =
            SymbolExtensions.GetMethodInfo(() => ConstructScaleUpTweenStub(0f, 0f, null!, 0f, default(global::EaseType), 0f));

        private static readonly MethodInfo ConstructScaleDownTween =
            SymbolExtensions.GetMethodInfo(() => ConstructScaleDownTweenStub(0f, 0f, null!, 0f, default(global::EaseType), 0f));

        private static readonly ConstructorInfo ConstructFloatTween =
            typeof(FloatTween).GetConstructor(new[]
            {
                typeof(float), typeof(float), typeof(Action<float>), typeof(float), typeof(global::EaseType), typeof(float)
            })!;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var matcher = new CodeMatcher(instructions)
                .MatchForward(false, new CodeMatch(OpCodes.Stfld, ScaleUpTweenField));
            if (matcher.IsInvalid)
            {
                MpChatLog.Warn("[MPChat][LobbyAvatar] ScaleAnimator::InitIfNeeded scale-up pattern not found; leaving IL unchanged.");
                return instructions;
            }

            matcher.MatchBack(false, new CodeMatch(OpCodes.Newobj, ConstructFloatTween));
            if (matcher.IsInvalid)
            {
                MpChatLog.Warn("[MPChat][LobbyAvatar] ScaleAnimator::InitIfNeeded scale-up newobj pattern not found; leaving IL unchanged.");
                return instructions;
            }

            matcher.Set(OpCodes.Callvirt, ConstructScaleUpTween)
                .MatchForward(false, new CodeMatch(OpCodes.Stfld, ScaleDownTweenField));
            if (matcher.IsInvalid)
            {
                MpChatLog.Warn("[MPChat][LobbyAvatar] ScaleAnimator::InitIfNeeded scale-down pattern not found; leaving IL unchanged.");
                return instructions;
            }

            matcher.MatchBack(false, new CodeMatch(OpCodes.Newobj, ConstructFloatTween));
            if (matcher.IsInvalid)
            {
                MpChatLog.Warn("[MPChat][LobbyAvatar] ScaleAnimator::InitIfNeeded scale-down newobj pattern not found; leaving IL unchanged.");
                return instructions;
            }

            return matcher.Set(OpCodes.Callvirt, ConstructScaleDownTween).InstructionEnumeration();
        }

        private static FloatTween ConstructScaleUpTweenStub(float fromValue, float toValue, Action<float> onUpdate,
            float duration, global::EaseType easeType, float delay) =>
            new FloatTween(0.05f, toValue, onUpdate, duration, easeType, delay);

        private static FloatTween ConstructScaleDownTweenStub(float fromValue, float toValue, Action<float> onUpdate,
            float duration, global::EaseType easeType, float delay) =>
            new FloatTween(fromValue, 0.05f, onUpdate, duration, easeType, delay);
    }

    [HarmonyPatch]
    internal static class ScaleAnimatorPatchHideInstant
    {
        private static MethodBase? TargetMethod() =>
            AccessTools.Method(typeof(global::ScaleAnimator), "HideInstant");

        private static readonly MethodInfo SetLocalScaleAttacher =
            SymbolExtensions.GetMethodInfo(() => SetLocalScaleAttacherStub(null!, Vector3.one));

        private static readonly MethodInfo SetLocalScaleMethod =
            typeof(Transform).GetProperty(nameof(Transform.localScale))!.SetMethod!;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var matcher = new CodeMatcher(instructions)
                .MatchForward(false, new CodeMatch(OpCodes.Callvirt, SetLocalScaleMethod));
            if (matcher.IsInvalid)
            {
                MpChatLog.Warn("[MPChat][LobbyAvatar] ScaleAnimator::HideInstant localScale pattern not found; leaving IL unchanged.");
                return instructions;
            }

            return matcher.Set(OpCodes.Callvirt, SetLocalScaleAttacher).InstructionEnumeration();
        }

        private static void SetLocalScaleAttacherStub(Transform transform, Vector3 scale)
        {
            var driver = transform.GetComponent<MpChatLobbyCustomAvatarDriver>();
            if (driver != null && driver.HasActiveCustomAvatar)
            {
                transform.localScale = Vector3.one;
                return;
            }

            transform.localScale = Vector3.one * 0.05f;
        }
    }
}
