using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultiplayerChat.Core;

// Lobby mirror / local custom avatars: read Custom Avatars TrackingRig (FPFC, controller, VR) when MP pedestal pose stays static.
internal static class MpChatLocalCaPoseSampler
{
    private static bool _reflectionReady;

    private static System.Type? _trackingRigType;

    private static System.Type? _genericNodeType;

    private static PropertyInfo? _headProperty;

    private static PropertyInfo? _leftHandProperty;

    private static PropertyInfo? _rightHandProperty;

    private static PropertyInfo? _nodeTransformProperty;

    private static object? _cachedRig;

    private static int _cachedRigSceneHandle = -1;

    internal static void InvalidateCache()
    {
        _cachedRig = null;
        _cachedRigSceneHandle = -1;
    }

    public static bool TryGetWorldDevicePoses(
        out Vector3 headPosition,
        out Quaternion headRotation,
        out Vector3 rightHandPosition,
        out Quaternion rightHandRotation,
        out Vector3 leftHandPosition,
        out Quaternion leftHandRotation)
    {
        headPosition = default;
        headRotation = Quaternion.identity;
        rightHandPosition = default;
        rightHandRotation = Quaternion.identity;
        leftHandPosition = default;
        leftHandRotation = Quaternion.identity;

        if (!EnsureReflection())
            return false;

        var rig = ResolveCachedRig();
        if (rig == null)
            return false;

        return TryGetNodeWorldPose(rig, _headProperty, out headPosition, out headRotation) &&
               TryGetNodeWorldPose(rig, _rightHandProperty, out rightHandPosition, out rightHandRotation) &&
               TryGetNodeWorldPose(rig, _leftHandProperty, out leftHandPosition, out leftHandRotation);
    }

    private static bool TryGetNodeWorldPose(
        object rig,
        PropertyInfo? nodeProperty,
        out Vector3 position,
        out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;

        if (nodeProperty == null || _nodeTransformProperty == null)
            return false;

        var node = nodeProperty.GetValue(rig, null);
        if (node == null)
            return false;

        if (_nodeTransformProperty.GetValue(node, null) is not Transform transform || transform == null)
            return false;

        position = transform.position;
        rotation = transform.rotation;
        return position.sqrMagnitude > 1e-8f;
    }

    private static object? ResolveCachedRig()
    {
        if (_trackingRigType == null && !EnsureReflection())
            return null;

        var scene = SceneManager.GetActiveScene();
        if (_cachedRig != null && _cachedRigSceneHandle == scene.handle)
            return _cachedRig;

        _cachedRig = Object.FindObjectOfType(_trackingRigType!);
        _cachedRigSceneHandle = scene.handle;
        return _cachedRig;
    }

    private static bool EnsureReflection()
    {
        if (_reflectionReady)
            return _trackingRigType != null;

        _reflectionReady = true;
        _trackingRigType = System.Type.GetType("CustomAvatar.Tracking.TrackingRig, CustomAvatar");
        if (_trackingRigType == null)
            return false;

        var flags = BindingFlags.Instance | BindingFlags.Public;
        _headProperty = _trackingRigType.GetProperty("head", flags);
        _leftHandProperty = _trackingRigType.GetProperty("leftHand", flags);
        _rightHandProperty = _trackingRigType.GetProperty("rightHand", flags);

        _genericNodeType = System.Type.GetType("CustomAvatar.Tracking.GenericNode, CustomAvatar");
        if (_genericNodeType != null)
            _nodeTransformProperty = _genericNodeType.GetProperty("transform", flags);

        return _headProperty != null && _leftHandProperty != null && _rightHandProperty != null &&
               _nodeTransformProperty != null;
    }
}
