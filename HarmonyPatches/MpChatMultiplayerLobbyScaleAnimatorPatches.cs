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
    internal static void Apply(Harmony harmony)
    {
        harmony.CreateClassProcessor(typeof(ScaleAnimatorPatchInitIfNeeded)).Patch();
        harmony.CreateClassProcessor(typeof(ScaleAnimatorPatchHideInstant)).Patch();
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
            return new CodeMatcher(instructions)
                .MatchForward(false, new CodeMatch(OpCodes.Stfld, ScaleUpTweenField))
                .MatchBack(false, new CodeMatch(OpCodes.Newobj, ConstructFloatTween))
                .Set(OpCodes.Callvirt, ConstructScaleUpTween)
                .MatchForward(false, new CodeMatch(OpCodes.Stfld, ScaleDownTweenField))
                .MatchBack(false, new CodeMatch(OpCodes.Newobj, ConstructFloatTween))
                .Set(OpCodes.Callvirt, ConstructScaleDownTween)
                .InstructionEnumeration();
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
            return new CodeMatcher(instructions)
                .MatchForward(false, new CodeMatch(OpCodes.Callvirt, SetLocalScaleMethod))
                .Set(OpCodes.Callvirt, SetLocalScaleAttacher)
                .InstructionEnumeration();
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
