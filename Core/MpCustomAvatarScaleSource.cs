using System;
using System.Reflection;
using MultiplayerChat.Settings;
using UnityEngine;

namespace MultiplayerChat.Core;

// Reads PlayerAvatarManager.scale after CA resize (same value as "Resized avatar with scale" in CA logs).
internal static class MpCustomAvatarScaleSource
{
    private const float MinScale = 0.25f;

    private const float MaxScale = 4f;

    private static Assembly? _customAvatarAssembly;

    private static Type? _playerAvatarManagerType;

    private static PropertyInfo? _managerScaleProperty;

    private static MethodInfo? _calculateAvatarScaleMethod;

    private static bool _reflectionReady;

    private static MonoBehaviour? _cachedManager;

    private static float _managerCacheTime;

    private const float ManagerCacheSeconds = 2f;

    internal static void InvalidateCachedManager()
    {
        _cachedManager = null;
        _managerCacheTime = 0f;
    }

    public static bool TryGetLocalAvatarScale(out float scale)
    {
        scale = 1f;
        if (!EnsureReflection())
            return false;

        var pam = ResolvePlayerAvatarManager();
        if (pam == null)
            return TryGetScaleFromSavedEyeHeight(out scale);

        if (TryGetScaleForManager(pam, null, out scale))
            return true;

        return TryGetScaleFromSavedEyeHeight(out scale);
    }

    internal static bool TryGetScaleForManager(object manager, float? eyeHeightMeters, out float scale)
    {
        scale = 1f;
        if (!EnsureReflection())
            return false;

        if (TryReadManagerScale(manager, out scale))
            return true;

        var eyeHeight = eyeHeightMeters;
        if (!eyeHeight.HasValue && ModSettings.TryGetLobbyCustomAvatarSavedEyeHeight(out var savedEye))
            eyeHeight = savedEye;

        if (eyeHeight.HasValue && TryCalculateScaleForEyeHeight(manager, eyeHeight.Value, out scale))
            return true;

        return false;
    }

    private static bool TryReadManagerScale(object manager, out float scale)
    {
        scale = 1f;
        if (_managerScaleProperty == null)
            return false;

        var managerScale = _managerScaleProperty.GetValue(manager, null);
        if (managerScale is not float ms || ms <= 0.001f)
            return false;

        scale = ClampScale(ms);
        return true;
    }

    private static bool TryCalculateScaleForEyeHeight(object manager, float eyeHeightMeters, out float scale)
    {
        scale = 1f;
        if (_calculateAvatarScaleMethod == null)
            return false;

        var scaleObj = _calculateAvatarScaleMethod.Invoke(manager, new object[] { eyeHeightMeters });
        if (scaleObj is not float calculated || calculated <= 0.001f)
            return false;

        scale = ClampScale(calculated);
        return true;
    }

    private static bool TryGetScaleFromSavedEyeHeight(out float scale)
    {
        scale = 1f;
        if (!ModSettings.TryGetLobbyCustomAvatarSavedEyeHeight(out var eyeHeight))
            return false;

        if (!EnsureReflection())
            return false;

        var pam = ResolvePlayerAvatarManager();
        if (pam == null)
            return false;

        return TryGetScaleForManager(pam, eyeHeight, out scale);
    }

    private static MonoBehaviour? ResolvePlayerAvatarManager()
    {
        var now = Time.realtimeSinceStartup;
        if (_cachedManager != null && now - _managerCacheTime < ManagerCacheSeconds)
            return _cachedManager;

        if (_playerAvatarManagerType == null)
            return null;

        _cachedManager = UnityEngine.Object.FindObjectOfType(_playerAvatarManagerType) as MonoBehaviour;
        _managerCacheTime = now;
        return _cachedManager;
    }

    private static Assembly? ResolveCustomAvatarAssembly()
    {
        if (_customAvatarAssembly != null)
            return _customAvatarAssembly;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, "CustomAvatar", StringComparison.OrdinalIgnoreCase))
                continue;

            _customAvatarAssembly = asm;
            return asm;
        }

        return null;
    }

    private static Type? GetCustomAvatarType(string fullName) =>
        ResolveCustomAvatarAssembly()?.GetType(fullName, throwOnError: false);

    private static bool EnsureReflection()
    {
        if (_reflectionReady)
            return _playerAvatarManagerType != null;

        _reflectionReady = true;

        if (ResolveCustomAvatarAssembly() == null)
            return false;

        _playerAvatarManagerType = GetCustomAvatarType("CustomAvatar.Player.PlayerAvatarManager");
        if (_playerAvatarManagerType == null)
            return false;

        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        _managerScaleProperty = _playerAvatarManagerType.GetProperty("scale", flags);
        _calculateAvatarScaleMethod = _playerAvatarManagerType.GetMethod(
            "CalculateAvatarScale",
            flags,
            null,
            new[] { typeof(float) },
            null);

        return true;
    }

    private static float ClampScale(float value) =>
        Mathf.Clamp(value, MinScale, MaxScale);
}
