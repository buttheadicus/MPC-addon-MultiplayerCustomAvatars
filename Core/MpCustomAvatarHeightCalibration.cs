using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using MultiplayerChat.Settings;
using UnityEngine;
using Assembly = System.Reflection.Assembly;

namespace MultiplayerChat.Core;

// Match Custom Avatars "Measure player height": measure -> OnPlayerHeightChanged -> scale -> ResizeCurrentAvatar -> lobby sync.
internal static class MpCustomAvatarHeightCalibration
{
    private const float MinEyeHeightMeters = 0.8f;

    private const float MaxEyeHeightMeters = 2.6f;

    private static bool _reflectionReady;

    private static Type? _playerAvatarManagerType;

    private static Type? _trackingRigType;

    private static Type? _generalSettingsHostType;

    private static Type? _armSpanMeasurerType;

    private static Type? _settingsViewControllerType;

    private static PropertyInfo? _generalSettingsHostProperty;

    private static PropertyInfo? _trackingRigEyeHeightProperty;

    private static MethodInfo? _measureHeightMethod;

    private static MethodInfo? _onPlayerHeightChangedMethod;

    private static MethodInfo? _resizeCurrentAvatarMethod;

    private static MethodInfo? _loadAvatarFromSettingsAsyncMethod;

    private static MethodInfo? _switchToAvatarAsyncMethod;

    private static PropertyInfo? _currentlySpawnedAvatarProperty;

    private static FieldInfo? _pamSettingsField;

    private static PropertyInfo? _settingsPlayerEyeHeightProperty;

    private static PropertyInfo? _observableValueProperty;

    private static MethodInfo? _beatSaberAutoSetHeightMethod;

    private static PropertyInfo? _beatSaberPlayerHeightValueProperty;

    private static PropertyInfo? _scaledEyeHeightProperty;

    private static PropertyInfo? _generalSettingsHostHeightProperty;

    private static object? _cachedMeasureHost;

    private static float _lastAppliedSavedEyeHeight = -1f;

    private static Assembly? _customAvatarAssembly;

    public static void Run() => MpCustomAvatarSyncManager.RunHeightCalibration();

    internal static void InvalidateAppliedCache() => _lastAppliedSavedEyeHeight = -1f;

    // PAM ResizeCurrentAvatar only affects currentlySpawnedAvatar in Container, not lobby pedestal spawns.
    internal static IEnumerator EnsurePamAvatarReadyCoroutine()
    {
        if (!EnsureReflection())
            yield break;

        var manager = ResolvePlayerAvatarManager();
        if (manager == null)
            yield break;

        if (HasPamSpawnedAvatar(manager))
            yield break;

        Task? loadTask = null;
        if (TryStartPamAvatarLoadFromModSettings(manager, out loadTask) && loadTask != null)
        {
            while (!loadTask.IsCompleted)
                yield return null;
        }

        if (HasPamSpawnedAvatar(manager))
            yield break;

        if (_loadAvatarFromSettingsAsyncMethod != null)
        {
            loadTask = _loadAvatarFromSettingsAsyncMethod.Invoke(manager, null) as Task;
            if (loadTask != null)
            {
                while (!loadTask.IsCompleted)
                    yield return null;
            }
        }
    }

    public static bool ApplySavedPresetIfAny(bool refreshLobbyAvatar = false)
    {
        if (!ModSettings.TryGetLobbyCustomAvatarSavedEyeHeight(out var eyeHeight))
            return false;

        if (!EnsureReflection())
            return false;

        var manager = ResolvePlayerAvatarManager();
        if (manager == null)
            return false;

        if (Mathf.Abs(_lastAppliedSavedEyeHeight - eyeHeight) < 0.001f)
        {
            if (refreshLobbyAvatar)
                RefreshLocalLobbyAvatar();
            return true;
        }

        try
        {
            ApplyPlayerHeightToCustomAvatars(manager, eyeHeight);
            _lastAppliedSavedEyeHeight = eyeHeight;
            MultiplayerChat.Plugin.Log?.Debug(
                $"[MPChat][CustomAvatars] Applied saved lobby eye height {eyeHeight:F2} m.");
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn(
                $"[MPChat][CustomAvatars] Saved eye height apply failed: {ex.Message}");
            return false;
        }

        if (refreshLobbyAvatar)
            RefreshLocalLobbyAvatar();

        return true;
    }

    public static bool TryRunCalibration()
    {
        _cachedMeasureHost = null;

        if (!EnsureReflection())
        {
            MultiplayerChat.Plugin.Log?.Warn(
                "[MPChat][CustomAvatars] Calibrate Height: Custom Avatars is not installed.");
            return false;
        }

        var manager = ResolvePlayerAvatarManager();
        if (manager == null)
        {
            if (TryMeasureBeatSaberOnlyHeight(out var menuEye, out var menuSource))
                return TrySaveMeasuredHeight(menuEye, menuSource, manager: null, customAvatarsAlreadyResized: false);

            MultiplayerChat.Plugin.Log?.Warn(
                "[MPChat][CustomAvatars] Calibrate Height: Custom Avatars player rig is not active in this scene.");
            return false;
        }

        var rig = ResolveTrackingRig();

        if (TryMeasureViaGeneralSettingsHost(manager, rig, out var hostEye, out var hostSource))
        {
            // Host measure runs CA's full resize chain; a second ApplyPlayerHeight pass skews scale (~half size on pedestals).
            return TrySaveMeasuredHeight(hostEye, hostSource, manager, customAvatarsAlreadyResized: true);
        }

        if (!TryMeasureEyeHeightMeters(manager, out var eyeHeight, out var source))
        {
            if (TryMeasureBeatSaberOnlyHeight(out var fallbackEye, out var fallbackSource))
                return TrySaveMeasuredHeight(fallbackEye, fallbackSource, manager, customAvatarsAlreadyResized: false);

            MultiplayerChat.Plugin.Log?.Warn(
                "[MPChat][CustomAvatars] Calibrate Height: could not read eye height. In VR, stand normally and try again. In FPFC, set height in Beat Saber Settings > Player Height (Auto), or test in VR.");
            return false;
        }

        ApplyPlayerHeightToCustomAvatars(manager, eyeHeight);
        return TrySaveMeasuredHeight(eyeHeight, source, manager, customAvatarsAlreadyResized: true);
    }

    internal static void RefreshLocalLobbyAvatar()
    {
        MpCustomAvatarScaleSource.InvalidateCachedManager();
        MpCustomAvatarSyncManager.BroadcastScaleThenMetadata();
        MpChatLobbyCustomAvatarDriver.ApplyLocalScaleToMirrorPedestals();
        MpChatLobbyCustomAvatarDriver.NotifyLocalAvatarSettingsChanged();
    }

    private static bool TrySaveMeasuredHeight(
        float eyeHeight,
        string source,
        object? manager,
        bool customAvatarsAlreadyResized)
    {
        try
        {
            if (manager != null && !customAvatarsAlreadyResized)
                ApplyPlayerHeightToCustomAvatars(manager, eyeHeight);

            ModSettings.SetLobbyCustomAvatarSavedEyeHeight(eyeHeight);
            _lastAppliedSavedEyeHeight = eyeHeight;

            var scale = 1f;
            if (manager != null)
            {
                MpCustomAvatarScaleSource.InvalidateCachedManager();
                MpCustomAvatarScaleSource.TryGetLocalAvatarScale(out scale);
            }

            var suffix = manager != null
                ? "saved for later lobbies"
                : "saved for later lobbies (will apply when Custom Avatars loads)";
            MultiplayerChat.Plugin.Log?.Info(
                $"[MPChat][CustomAvatars] Calibrate Height: {eyeHeight:F2} m (scale {scale:F3}) from {source} ({suffix}).");
            return true;
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Warn(
                $"[MPChat][CustomAvatars] Calibrate Height failed: {ex.Message}");
            return false;
        }
    }

    private static void ApplyPlayerHeightToCustomAvatars(object manager, float playerHeight)
    {
        TrySetPlayerEyeHeightInPamSettings(manager, playerHeight);
        _onPlayerHeightChangedMethod?.Invoke(manager, new object[] { playerHeight });
        _resizeCurrentAvatarMethod?.Invoke(manager, null);
    }

    private static bool HasPamSpawnedAvatar(object manager)
    {
        if (_currentlySpawnedAvatarProperty == null)
            return false;

        return _currentlySpawnedAvatarProperty.GetValue(manager, null) != null;
    }

    private static bool TryStartPamAvatarLoadFromModSettings(object manager, out Task? task)
    {
        task = null;
        if (_switchToAvatarAsyncMethod == null)
            return false;

        var rel = ModSettings.LobbyCustomAvatarRelativePath.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(rel))
            return false;

        var full = Path.Combine(
            BeatSaberPaths.CustomAvatarsDirectory,
            rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
            return false;

        task = _switchToAvatarAsyncMethod.Invoke(manager, new object?[] { full, null }) as Task;
        return task != null;
    }

    private static bool TryMeasureBeatSaberOnlyHeight(out float eyeHeightMeters, out string source)
    {
        eyeHeightMeters = 0f;
        source = "";

        TryBeatSaberAutoSetPlayerHeight();

        if (TryReadBeatSaberPlayerHeightSettings(out eyeHeightMeters))
        {
            source = "Beat Saber player height settings";
            return true;
        }

        var detector = UnityEngine.Object.FindObjectOfType<PlayerHeightDetector>();
        if (detector != null && IsValidEyeHeight(detector.playerHeight))
        {
            eyeHeightMeters = detector.playerHeight;
            source = "PlayerHeightDetector";
            return true;
        }

        return false;
    }

    private static object? ResolveTrackingRig()
    {
        if (_trackingRigType == null)
            return null;

        return FindUnityObject(_trackingRigType);
    }

    private static bool TryMeasureViaGeneralSettingsHost(object manager, object? rig, out float eyeHeightMeters, out string source)
    {
        eyeHeightMeters = 0f;
        source = "";

        if (_measureHeightMethod == null)
            return false;

        var host = ResolveGeneralSettingsHost(manager, rig);
        if (host == null)
            return false;

        try
        {
            _measureHeightMethod.Invoke(host, null);
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Debug(
                $"[MPChat][CustomAvatars] OnMeasureHeightButtonClicked failed: {ex.Message}");
            return false;
        }

        if (_generalSettingsHostHeightProperty?.GetValue(host, null) is float hostHeight && IsValidEyeHeight(hostHeight))
        {
            eyeHeightMeters = hostHeight;
            source = "Custom Avatars GeneralSettingsHost";
            return true;
        }

        return TryReadMeasuredEyeHeight(manager, out eyeHeightMeters, out source);
    }

    private static object? ResolveGeneralSettingsHost(object manager, object? rig)
    {
        if (_cachedMeasureHost != null)
            return _cachedMeasureHost;

        if (_settingsViewControllerType != null && _generalSettingsHostProperty != null)
        {
            var settingsView = FindUnityObject(_settingsViewControllerType, includeInactive: true);
            if (settingsView != null)
            {
                var existing = _generalSettingsHostProperty.GetValue(settingsView, null);
                if (existing != null)
                {
                    _cachedMeasureHost = existing;
                    return existing;
                }
            }
        }

        if (_generalSettingsHostType == null ||
            _pamSettingsField == null ||
            _armSpanMeasurerType == null ||
            _trackingRigType == null)
            return null;

        rig ??= ResolveTrackingRig();
        if (rig == null)
            return null;

        var settings = _pamSettingsField.GetValue(manager);
        if (settings == null)
            return null;

        object? host;
        try
        {
            var armSpanMeasurer = Activator.CreateInstance(_armSpanMeasurerType);
            if (armSpanMeasurer == null)
                return null;

            var ctor = _generalSettingsHostType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { _pamSettingsField.FieldType, _trackingRigType!, _armSpanMeasurerType },
                null);
            if (ctor == null)
                return null;

            host = ctor.Invoke(new[] { settings, rig, armSpanMeasurer });
        }
        catch (Exception ex)
        {
            MultiplayerChat.Plugin.Log?.Debug(
                $"[MPChat][CustomAvatars] Could not bootstrap GeneralSettingsHost: {ex.Message}");
            return null;
        }

        if (host == null)
            return null;

        _cachedMeasureHost = host;
        return host;
    }

    private static bool TryReadMeasuredEyeHeight(object manager, out float eyeHeightMeters, out string source)
    {
        eyeHeightMeters = 0f;
        source = "";

        if (_trackingRigType != null && _trackingRigEyeHeightProperty != null)
        {
            var rig = FindUnityObject(_trackingRigType);
            if (rig != null &&
                _trackingRigEyeHeightProperty.GetValue(rig, null) is float rigEye &&
                IsValidEyeHeight(rigEye))
            {
                eyeHeightMeters = rigEye;
                source = "Custom Avatars measure (TrackingRig)";
                return true;
            }
        }

        if (_pamSettingsField != null && _settingsPlayerEyeHeightProperty != null && _observableValueProperty != null)
        {
            var settings = _pamSettingsField.GetValue(manager);
            if (settings != null)
            {
                var observable = _settingsPlayerEyeHeightProperty.GetValue(settings, null);
                if (observable != null &&
                    _observableValueProperty.GetValue(observable, null) is float settingsEye &&
                    IsValidEyeHeight(settingsEye))
                {
                    eyeHeightMeters = settingsEye;
                    source = "Custom Avatars measure (settings)";
                    return true;
                }
            }
        }

        return false;
    }

    private static void TrySetPlayerEyeHeightInPamSettings(object manager, float eyeHeight)
    {
        if (_pamSettingsField == null || _settingsPlayerEyeHeightProperty == null || _observableValueProperty == null)
            return;

        var settings = _pamSettingsField.GetValue(manager);
        if (settings == null)
            return;

        var observable = _settingsPlayerEyeHeightProperty.GetValue(settings, null);
        if (observable == null)
            return;

        _observableValueProperty.SetValue(observable, eyeHeight, null);
    }

    private static bool TryMeasureEyeHeightMeters(object manager, out float eyeHeightMeters, out string source)
    {
        eyeHeightMeters = 0f;
        source = "";

        TryBeatSaberAutoSetPlayerHeight();

        if (_scaledEyeHeightProperty?.GetValue(manager, null) is float scaledEye && IsValidEyeHeight(scaledEye))
        {
            eyeHeightMeters = scaledEye;
            source = "Custom Avatars scaledEyeHeight";
            return true;
        }

        if (_trackingRigType != null && _trackingRigEyeHeightProperty != null)
        {
            var rig = FindUnityObject(_trackingRigType);
            if (rig != null &&
                _trackingRigEyeHeightProperty.GetValue(rig, null) is float rigEye &&
                IsValidEyeHeight(rigEye))
            {
                eyeHeightMeters = rigEye;
                source = "Custom Avatars TrackingRig";
                return true;
            }
        }

        if (TryReadBeatSaberPlayerHeightSettings(out var settingsHeight))
        {
            eyeHeightMeters = settingsHeight;
            source = "Beat Saber player height settings";
            return true;
        }

        var detector = UnityEngine.Object.FindObjectOfType<PlayerHeightDetector>();
        if (detector != null && IsValidEyeHeight(detector.playerHeight))
        {
            eyeHeightMeters = detector.playerHeight;
            source = "PlayerHeightDetector";
            return true;
        }

        return false;
    }

    private static void TryBeatSaberAutoSetPlayerHeight()
    {
        if (_beatSaberAutoSetHeightMethod == null)
            return;

        var controller = UnityEngine.Object.FindObjectOfType<PlayerHeightSettingsController>();
        if (controller == null)
            return;

        try
        {
            _beatSaberAutoSetHeightMethod.Invoke(controller, null);
        }
        catch
        {
            /* menu object may not be ready */
        }
    }

    private static bool TryReadBeatSaberPlayerHeightSettings(out float heightMeters)
    {
        heightMeters = 0f;
        if (_beatSaberPlayerHeightValueProperty == null)
            return false;

        var controller = UnityEngine.Object.FindObjectOfType<PlayerHeightSettingsController>();
        if (controller == null)
            return false;

        var val = _beatSaberPlayerHeightValueProperty.GetValue(controller, null);
        if (val is not float f)
            return false;

        heightMeters = f;
        return IsValidEyeHeight(heightMeters);
    }

    private static bool IsValidEyeHeight(float meters) =>
        meters >= MinEyeHeightMeters && meters <= MaxEyeHeightMeters;

    private static object? ResolvePlayerAvatarManager()
    {
        if (_playerAvatarManagerType == null)
            return null;

        return FindUnityObject(_playerAvatarManagerType);
    }

    private static UnityEngine.Object? FindUnityObject(Type type, bool includeInactive = false)
    {
        if (!typeof(UnityEngine.Object).IsAssignableFrom(type))
            return null;

        return UnityEngine.Object.FindObjectOfType(type, includeInactive);
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

        var assembly = ResolveCustomAvatarAssembly();
        if (assembly == null)
            return false;

        _playerAvatarManagerType = GetCustomAvatarType("CustomAvatar.Player.PlayerAvatarManager");
        if (_playerAvatarManagerType == null)
            return false;

        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        _trackingRigType = GetCustomAvatarType("CustomAvatar.Tracking.TrackingRig");
        if (_trackingRigType != null)
            _trackingRigEyeHeightProperty = _trackingRigType.GetProperty("eyeHeight", flags);

        _onPlayerHeightChangedMethod = _playerAvatarManagerType.GetMethod(
            "OnPlayerHeightChanged",
            flags,
            null,
            new[] { typeof(float) },
            null);

        _resizeCurrentAvatarMethod = _playerAvatarManagerType.GetMethod(
            "ResizeCurrentAvatar",
            flags,
            null,
            Type.EmptyTypes,
            null);

        _loadAvatarFromSettingsAsyncMethod = _playerAvatarManagerType.GetMethod(
            "LoadAvatarFromSettingsAsync",
            flags,
            null,
            Type.EmptyTypes,
            null);

        _switchToAvatarAsyncMethod = _playerAvatarManagerType.GetMethod(
            "SwitchToAvatarAsync",
            flags,
            null,
            new[] { typeof(string), typeof(IProgress<float>) },
            null);

        _currentlySpawnedAvatarProperty = _playerAvatarManagerType.GetProperty("currentlySpawnedAvatar", flags);
        _pamSettingsField = _playerAvatarManagerType.GetField("_settings", flags);

        var settingsType = GetCustomAvatarType("CustomAvatar.Configuration.Settings");
        if (settingsType != null)
        {
            _settingsPlayerEyeHeightProperty = settingsType.GetProperty("playerEyeHeight", flags);
            var observableType = GetCustomAvatarType("CustomAvatar.Configuration.ObservableValue`1");
            if (observableType != null)
            {
                var observableFloat = observableType.MakeGenericType(typeof(float));
                _observableValueProperty = observableFloat.GetProperty("value", flags);
            }
        }

        _settingsViewControllerType = GetCustomAvatarType("CustomAvatar.UI.SettingsViewController");
        _generalSettingsHostType = GetCustomAvatarType("CustomAvatar.UI.GeneralSettingsHost");
        _armSpanMeasurerType = GetCustomAvatarType("CustomAvatar.UI.ArmSpanMeasurer");
        if (_settingsViewControllerType != null)
            _generalSettingsHostProperty = _settingsViewControllerType.GetProperty("generalSettingsHost", flags);

        if (_generalSettingsHostType != null)
        {
            _measureHeightMethod = _generalSettingsHostType.GetMethod("OnMeasureHeightButtonClicked", flags);
            _generalSettingsHostHeightProperty = _generalSettingsHostType.GetProperty("height", flags);
        }

        _scaledEyeHeightProperty = _playerAvatarManagerType.GetProperty("scaledEyeHeight", flags);

        var phscType = typeof(PlayerHeightSettingsController);
        _beatSaberAutoSetHeightMethod = phscType.GetMethod("AutoSetHeight", flags);
        _beatSaberPlayerHeightValueProperty = phscType.GetProperty("value", flags);

        return true;
    }
}
