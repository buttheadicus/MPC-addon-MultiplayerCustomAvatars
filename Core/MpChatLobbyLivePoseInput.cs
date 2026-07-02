using BeatSaber.AvatarCore;

using CustomAvatar.Tracking;

using System;

using System.Reflection;

using UnityEngine;



namespace MultiplayerChat.Core;



internal sealed class MpChatLobbyLivePoseInput : IAvatarInput

{

    private const float MinBoneOffset = 0.12f;

    // Reject collapsed multiplayer bones (intro / remote FPFC before first pose packet).
    private const float MinHeadToHandSeparationSq = 0.0025f;

    // Tune multiplayer hand rotation (degrees, applied after saber alignment in HandPoseKnucklesAlongSaber).
    private static readonly Vector3 RightHandRotationOffsetEuler = new(0f, 0f, 90f);

    private static readonly Vector3 LeftHandRotationOffsetEuler = new(0f, 0f, -90f);

    // Hand position nudge in controller local space (meters): X right, Y up, Z forward along the device/saber.
    private static readonly Vector3 RightHandPositionOffsetControllerLocal = Vector3.zero;

    private static readonly Vector3 LeftHandPositionOffsetControllerLocal = Vector3.zero;



    private Action? _inputChangedHandlers;



    event Action IAvatarInput.inputChanged

    {

        add => _inputChangedHandlers += value;

        remove => _inputChangedHandlers -= value;

    }



    // Let the rig rise when the head is above a fully extended stance (feet stay grounded in CA otherwise).
    public bool allowMaintainPelvisPosition => false;



    private MultiplayerAvatarPoseController _poseController;



    private Transform _headTransform;

    private Transform _rightHandTransform;

    private Transform _leftHandTransform;

    private Transform _bodyTransform;



    private readonly Transform _proxyHead;

    private readonly Transform _proxyRight;

    private readonly Transform _proxyLeft;



    private UnityEngine.Pose _head = new();

    private UnityEngine.Pose _rightHand = new();

    private UnityEngine.Pose _leftHand = new();

    private bool _hasValidPose;

    private Vector3 _lastNotifiedHeadWorld;

    private Vector3 _lastNotifiedRightWorld;

    private Vector3 _lastNotifiedLeftWorld;

    private Vector3 _polledRightWorld;

    private Vector3 _polledLeftWorld;

    private bool _useLocalCustomAvatarTracking;

    private bool _loggedLocalTrackingUnavailable;

    private int _lastPoseEventFrame = -1;



    internal void EnableLocalCustomAvatarTracking(bool enable) =>
        _useLocalCustomAvatarTracking = enable;



    internal void RegisterForPoll() => MpChatLobbyPosePoll.Register(this);



    internal void UnregisterFromPoll() => MpChatLobbyPosePoll.Unregister(this);



    internal MpChatLobbyLivePoseInput(MultiplayerAvatarPoseController poseController)

    {

        _poseController = poseController;



        _poseController.didUpdatePoseEvent += OnInputChanged;

        _headTransform = ReqTransformField(poseController, "_headTransform");

        _rightHandTransform = ReqTransformField(poseController, "_rightSaberTransform");

        _leftHandTransform = ReqTransformField(poseController, "_leftSaberTransform");

        _bodyTransform = poseController.transform.Find("Body") ?? poseController.transform;



        var root = poseController.transform;

        _proxyHead = new GameObject("MpChatLobbyPoseProxyHead").transform;

        _proxyHead.SetParent(root, false);

        _proxyRight = new GameObject("MpChatLobbyPoseProxyRH").transform;

        _proxyRight.SetParent(root, false);

        _proxyLeft = new GameObject("MpChatLobbyPoseProxyLH").transform;

        _proxyLeft.SetParent(root, false);

    }



    internal void Retarget(MultiplayerAvatarPoseController poseController)

    {

        if (poseController == _poseController)

            return;



        _poseController.didUpdatePoseEvent -= OnInputChanged;

        _poseController = poseController;

        _poseController.didUpdatePoseEvent += OnInputChanged;



        _headTransform = ReqTransformField(poseController, "_headTransform");

        _rightHandTransform = ReqTransformField(poseController, "_rightSaberTransform");

        _leftHandTransform = ReqTransformField(poseController, "_leftSaberTransform");

        _bodyTransform = poseController.transform.Find("Body") ?? poseController.transform;



        var root = poseController.transform;

        _proxyHead.SetParent(root, false);

        _proxyRight.SetParent(root, false);

        _proxyLeft.SetParent(root, false);



        PushPoseToProxies();

    }



    private static Transform ReqTransformField(MultiplayerAvatarPoseController owner, string fieldName)

    {

        var f = typeof(MultiplayerAvatarPoseController).GetField(fieldName,

            BindingFlags.Instance | BindingFlags.NonPublic);

        if (f?.GetValue(owner) is Transform t)

            return t;



        throw new MissingFieldException(nameof(MultiplayerAvatarPoseController), fieldName);

    }



    public void SetEnabled(bool enabled)

    {

        if (_headTransform != null)

            SafeSetActive(_headTransform.gameObject, !enabled);



        // Never deactivate the pose root; custom avatar is parented there.

        if (_bodyTransform != null && _bodyTransform != _poseController.transform)

            SafeSetActive(_bodyTransform.gameObject, !enabled);



        var rh = _rightHandTransform.Find("Hand");

        var lh = _leftHandTransform.Find("Hand");

        if (rh != null)

            SafeSetActive(rh.gameObject, !enabled);

        if (lh != null)

            SafeSetActive(lh.gameObject, !enabled);

    }



    private static void SafeSetActive(GameObject? go, bool active)

    {

        if (go == null || go.activeSelf == active)

            return;



        try

        {

            go.SetActive(active);

        }

        catch

        {

        }

    }



    internal void SetVanillaRenderersVisible(bool visible)

    {

        SetRenderersOnTransform(_headTransform, visible);

        if (_bodyTransform != _poseController.transform)

            SetRenderersOnTransform(_bodyTransform, visible);

    }



    private static void SetRenderersOnTransform(Transform? root, bool visible)

    {

        if (root == null)

            return;



        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))

        {

            if (renderer != null)

                renderer.enabled = visible;

        }

    }



    internal void SeedInitialPose()

    {

        PushPoseToProxies();

        if (_hasValidPose)

            NotifyInputChangedIfNeeded(force: true);

    }



    internal void PollThrottled()

    {

        if (!_useLocalCustomAvatarTracking && Time.frameCount - _lastPoseEventFrame <= 1)

            return;



        PushPoseToProxies();

        NotifyInputChangedIfNeeded(force: false);

    }



    private void OnInputChanged(Vector3 newHeadPosition)

    {

        _ = newHeadPosition;

        _lastPoseEventFrame = Time.frameCount;

        PushPoseToProxies();

        NotifyInputChangedIfNeeded(force: false);

    }



    private void NotifyInputChangedIfNeeded(bool force)

    {

        if (!_hasValidPose)

            return;



        var headWorld = _headTransform.position;

        if (!force &&

            (headWorld - _lastNotifiedHeadWorld).sqrMagnitude < 1e-8f &&

            (_polledRightWorld - _lastNotifiedRightWorld).sqrMagnitude < 1e-8f &&

            (_polledLeftWorld - _lastNotifiedLeftWorld).sqrMagnitude < 1e-8f)

            return;



        _lastNotifiedHeadWorld = headWorld;

        _lastNotifiedRightWorld = _polledRightWorld;

        _lastNotifiedLeftWorld = _polledLeftWorld;

        _inputChangedHandlers?.Invoke();

    }



    private void PushPoseToProxies()

    {

        if (_headTransform == null || _rightHandTransform == null || _leftHandTransform == null)

            return;



        var root = _poseController.transform;

        if (_useLocalCustomAvatarTracking && TryPushFromLocalCustomAvatarTracking(root))

            return;



        var headWorld = _headTransform.position;

        var rightWorld = HandSampleWorldPosition(_rightHandTransform);

        var leftWorld = HandSampleWorldPosition(_leftHandTransform);

        if (ShouldDeferMulticastPoseDrive(headWorld, rightWorld, leftWorld))

        {

            _hasValidPose = false;

            return;

        }

        _head = BonePoseInAvatarRootSpace(root, _headTransform, new Vector3(0f, 1.6f, 0f), Quaternion.identity);

        _rightHand = HandPoseKnucklesAlongSaber(

            root,

            _rightHandTransform,

            rightWorld,

            isRight: true,

            _head.position + Vector3.right * MinBoneOffset,

            _head.rotation);

        _leftHand = HandPoseKnucklesAlongSaber(

            root,

            _leftHandTransform,

            leftWorld,

            isRight: false,

            _head.position + Vector3.left * MinBoneOffset,

            _head.rotation);



        if ((_rightHand.position - _head.position).sqrMagnitude < 1e-6f)

            _rightHand.position = _head.position + Vector3.right * MinBoneOffset;

        if ((_leftHand.position - _head.position).sqrMagnitude < 1e-6f)

            _leftHand.position = _head.position + Vector3.left * MinBoneOffset;



        _proxyHead.localPosition = _head.position;

        _proxyHead.localRotation = _head.rotation;

        _proxyRight.localPosition = _rightHand.position;

        _proxyRight.localRotation = _rightHand.rotation;

        _proxyLeft.localPosition = _leftHand.position;

        _proxyLeft.localRotation = _leftHand.rotation;

        _polledRightWorld = rightWorld;

        _polledLeftWorld = leftWorld;

        _hasValidPose = true;

    }



    private bool IsArenaContext() =>

        string.Equals(_poseController.gameObject.scene.name, "GameCore", StringComparison.Ordinal);



    private bool ShouldDeferMulticastPoseDrive(Vector3 headWorld, Vector3 rightWorld, Vector3 leftWorld)

    {

        if (MpChatFeatures.LobbyDeferCollapsedRemoteBones &&
            !HasValidBoneSeparation(headWorld, rightWorld, leftWorld))

            return true;



        if (IsArenaContext() && MpChatArenaAvatarAttach.IsUnderBigAvatarIntro(_poseController.transform))

            return true;



        return false;

    }



    private static bool HasValidBoneSeparation(Vector3 headWorld, Vector3 rightWorld, Vector3 leftWorld)

    {

        return (rightWorld - headWorld).sqrMagnitude >= MinHeadToHandSeparationSq

            && (leftWorld - headWorld).sqrMagnitude >= MinHeadToHandSeparationSq

            && (rightWorld - leftWorld).sqrMagnitude >= 1e-6f;

    }



    private bool TryPushFromLocalCustomAvatarTracking(Transform root)
    {
        if (!MpChatLocalCaPoseSampler.TryGetWorldDevicePoses(
                out var headWorld,
                out var headWorldRot,
                out var rightWorld,
                out var rightWorldRot,
                out var leftWorld,
                out var leftWorldRot))
        {
            if (!_loggedLocalTrackingUnavailable)
            {
                _loggedLocalTrackingUnavailable = true;
                MultiplayerChat.Plugin.Log?.Debug(
                    "[MPChat][LobbyAvatar] Local Custom Avatars tracking rig not available; using multiplayer pose bones.");
            }

            return false;
        }

        _hasValidPose = true;
        _head = WorldDevicePoseInAvatarRootSpace(
            root,
            headWorld,
            headWorldRot,
            new Vector3(0f, 1.6f, 0f),
            Quaternion.identity);
        _rightHand = WorldDevicePoseInAvatarRootSpace(
            root,
            ApplyHandPositionOffsetControllerWorld(rightWorld, rightWorldRot, isRight: true),
            rightWorldRot * Quaternion.Euler(RightHandRotationOffsetEuler),
            _head.position + Vector3.right * MinBoneOffset,
            _head.rotation);
        _leftHand = WorldDevicePoseInAvatarRootSpace(
            root,
            ApplyHandPositionOffsetControllerWorld(leftWorld, leftWorldRot, isRight: false),
            leftWorldRot * Quaternion.Euler(LeftHandRotationOffsetEuler),
            _head.position + Vector3.left * MinBoneOffset,
            _head.rotation);

        _proxyHead.localPosition = _head.position;
        _proxyHead.localRotation = _head.rotation;
        _proxyRight.localPosition = _rightHand.position;
        _proxyRight.localRotation = _rightHand.rotation;
        _proxyLeft.localPosition = _leftHand.position;
        _proxyLeft.localRotation = _leftHand.rotation;
        _polledRightWorld = rightWorld;
        _polledLeftWorld = leftWorld;
        return true;
    }



    private static UnityEngine.Pose WorldDevicePoseInAvatarRootSpace(
        Transform root,
        Vector3 worldPosition,
        Quaternion worldRotation,
        Vector3 localFallbackPosition,
        Quaternion localFallbackRotation)
    {
        var localPos = root.InverseTransformPoint(worldPosition);
        var localRot = Quaternion.Inverse(root.rotation) * worldRotation;
        if (localPos.sqrMagnitude < 1e-6f)
            localPos = localFallbackPosition;
        if (!IsValidRotation(localRot))
            localRot = localFallbackRotation;

        return new UnityEngine.Pose(localPos, localRot);
    }

    private static Vector3 HandSampleWorldPosition(Transform saberTransform)
    {
        var hand = saberTransform.Find("Hand");
        return hand != null ? hand.position : saberTransform.position;
    }

    private static Quaternion ControllerRotationForHandOffset(Transform saberTransform)
    {
        var hand = saberTransform.Find("Hand");
        return hand != null ? hand.rotation : saberTransform.rotation;
    }



    private static Vector3 ApplyHandPositionOffsetControllerWorld(
        Vector3 worldPosition,
        Quaternion controllerWorldRotation,
        bool isRight)
    {
        var offset = isRight
            ? RightHandPositionOffsetControllerLocal
            : LeftHandPositionOffsetControllerLocal;
        if (offset.sqrMagnitude < 1e-8f)
            return worldPosition;
        return worldPosition + controllerWorldRotation * offset;
    }



    private static Vector3 SaberBladeAxisWorld(Transform saberTransform)

    {

        var up = saberTransform.up;

        if (up.sqrMagnitude > 0.25f)

            return up.normalized;

        var forward = saberTransform.forward;

        return forward.sqrMagnitude > 0.25f ? forward.normalized : Vector3.forward;

    }



    private static UnityEngine.Pose HandPoseKnucklesAlongSaber(

        Transform root,

        Transform saberTransform,

        Vector3 handWorldPosition,

        bool isRight,

        Vector3 localFallbackPosition,

        Quaternion localFallbackRotation)

    {

        handWorldPosition = ApplyHandPositionOffsetControllerWorld(
            handWorldPosition,
            ControllerRotationForHandOffset(saberTransform),
            isRight);

        var localPos = root.InverseTransformPoint(handWorldPosition);

        if (localPos.sqrMagnitude < 1e-6f)

            localPos = localFallbackPosition;



        var blade = SaberBladeAxisWorld(saberTransform);

        var palmUp = isRight ? -saberTransform.right : saberTransform.right;

        if (palmUp.sqrMagnitude < 1e-4f)

            palmUp = Vector3.up;

        else

            palmUp.Normalize();



        Quaternion worldRot;

        worldRot = SafeLookRotation(blade, palmUp, saberTransform.rotation);



        var offsetEuler = isRight ? RightHandRotationOffsetEuler : LeftHandRotationOffsetEuler;

        worldRot *= Quaternion.Euler(offsetEuler);



        var localRot = Quaternion.Inverse(root.rotation) * worldRot;

        if (!IsValidRotation(localRot))

            localRot = localFallbackRotation;



        return new UnityEngine.Pose(localPos, localRot);

    }



    private static UnityEngine.Pose BonePoseInAvatarRootSpace(

        Transform root,

        Transform bone,

        Vector3 localFallbackPosition,

        Quaternion localFallbackRotation)

    {

        var localPos = root.InverseTransformPoint(bone.position);

        var localRot = Quaternion.Inverse(root.rotation) * bone.rotation;

        if (localPos.sqrMagnitude < 1e-6f)

            localPos = localFallbackPosition;

        if (!IsValidRotation(localRot))

            localRot = localFallbackRotation;



        return new UnityEngine.Pose(localPos, localRot);

    }



    private static bool IsValidRotation(Quaternion q)

    {

        if (q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f)

            return false;

        return !float.IsNaN(q.x) && !float.IsNaN(q.y) && !float.IsNaN(q.z) && !float.IsNaN(q.w);

    }



    private static Quaternion SafeLookRotation(Vector3 forward, Vector3 upwards, Quaternion fallback)

    {

        if (forward.sqrMagnitude < 1e-8f)

            return fallback;

        forward.Normalize();

        if (upwards.sqrMagnitude < 1e-8f)

            return Quaternion.LookRotation(forward);

        upwards.Normalize();

        if (Vector3.Cross(forward, upwards).sqrMagnitude < 1e-8f)

            return Quaternion.LookRotation(forward);

        return Quaternion.LookRotation(forward, upwards);

    }



    public bool TryGetFingerCurl(DeviceUse use, out FingerCurl curl)

    {

        curl = new FingerCurl(0f, 0f, 0f, 0f, 0f);

        return false;

    }



    public bool TryGetTransform(DeviceUse use, out Transform transform)

    {

        switch (use)

        {

            case DeviceUse.Head:

                transform = _proxyHead;

                return true;

            case DeviceUse.RightHand:

                transform = _proxyRight;

                return true;

            case DeviceUse.LeftHand:

                transform = _proxyLeft;

                return true;

            default:

                transform = null!;

                return false;

        }

    }

}
