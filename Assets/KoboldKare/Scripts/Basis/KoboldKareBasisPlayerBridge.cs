using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

/// <summary>
/// Binds the Basis native local-player/XR runtime to the currently possessed Kobold.
///
/// KoboldKare remains authoritative for gameplay physics and locomotion. Basis supplies the
/// headset/controller poses, XR camera and controller input. The hidden Basis player root is kept
/// at the Kobold's feet so Basis' tracking space follows the gameplay body without running a second
/// character controller.
/// </summary>
[DefaultExecutionOrder(-900)]
public sealed class KoboldKareBasisPlayerBridge : MonoBehaviour {
    private const float ButtonThreshold = 0.55f;

    private PlayerPossession possession;
    private KoboldCharacterController koboldController;
    private CharacterControllerAnimator characterAnimator;
    private Grabber grabber;
    private PrecisionGrabber precisionGrabber;
    private CameraSwitcher cameraSwitcher;
    private HandIK handIK;
    private Transform defaultGrabView;

    private BasisLocalPlayer basisPlayer;
    private BasisInput leftInput;
    private BasisInput rightInput;

    private Transform leftHandProxy;
    private Transform rightHandProxy;
    private Transform rightAimProxy;

    private Camera koboldCamera;
    private AudioListener koboldAudioListener;
    private bool koboldCameraWasEnabled;
    private bool koboldAudioListenerWasEnabled;
    private bool fpsCanvasWasActive;
    private bool cameraSwitcherWasEnabled;
    private bool vrActive;
    private bool bound;

    private bool previousGrab;
    private bool previousActivate;
    private bool previousUse;
    private bool previousUnfreeze;
    private float nextDeviceRefreshTime;

    private readonly List<RendererState> hiddenBasisRenderers = new List<RendererState>();

    private readonly struct RendererState {
        public readonly Renderer Renderer;
        public readonly bool Enabled;

        public RendererState(Renderer renderer, bool enabled) {
            Renderer = renderer;
            Enabled = enabled;
        }
    }

    private void Awake() {
        possession = GetComponent<PlayerPossession>();
        koboldController = GetComponentInParent<KoboldCharacterController>();
        characterAnimator = GetComponentInParent<CharacterControllerAnimator>();
        grabber = GetComponentInParent<Grabber>();
        precisionGrabber = GetComponentInParent<PrecisionGrabber>();
        cameraSwitcher = GetComponent<CameraSwitcher>();

        Animator playerModel = characterAnimator != null ? characterAnimator.GetPlayerModel() : null;
        if (playerModel != null) {
            handIK = playerModel.GetComponent<HandIK>();
            defaultGrabView = playerModel.GetBoneTransform(HumanBodyBones.Head);
        }

        leftHandProxy = CreateProxy("Basis Left Hand");
        rightHandProxy = CreateProxy("Basis Right Hand");
        rightAimProxy = CreateProxy("Basis Right Aim");
    }

    private void OnEnable() {
        BasisLocalPlayer.OnLocalPlayerInitialized += OnBasisLocalPlayerInitialized;
        BasisLocalPlayer.OnLocalAvatarChanged += OnBasisLocalAvatarChanged;
        BasisDeviceManagement.OnInitializationCompleted += OnBasisDeviceInitializationCompleted;
        BasisDeviceManagement.OnBootModeChanged += OnBasisBootModeChanged;
        TryBindBasisPlayer();
    }

    private void OnDisable() {
        BasisLocalPlayer.OnLocalPlayerInitialized -= OnBasisLocalPlayerInitialized;
        BasisLocalPlayer.OnLocalAvatarChanged -= OnBasisLocalAvatarChanged;
        BasisDeviceManagement.OnInitializationCompleted -= OnBasisDeviceInitializationCompleted;
        BasisDeviceManagement.OnBootModeChanged -= OnBasisBootModeChanged;
        SetVRActive(false);
        RestoreBasisAvatarRenderers();

        if (bound && basisPlayer != null && basisPlayer.LocalCharacterDriver != null) {
            basisPlayer.LocalCharacterDriver.IsEnabled = true;
        }
        if (BasisLocalCameraDriver.CameraInstance != null) {
            BasisLocalCameraDriver.CameraInstance.enabled = true;
        }
        bound = false;
        basisPlayer = null;
        leftInput = null;
        rightInput = null;
    }

    private void Update() {
        if (!bound) {
            TryBindBasisPlayer();
            if (!bound) {
                return;
            }
        }

        bool shouldUseVR = BasisDeviceManagement.IsCurrentModeVR();
        if (shouldUseVR != vrActive) {
            SetVRActive(shouldUseVR);
        }

        // KoboldKare owns locomotion in both desktop and VR. Basis' local player is only a
        // tracking/camera rig here, so keep its own character driver disabled and anchored.
        if (basisPlayer.LocalCharacterDriver != null) {
            basisPlayer.LocalCharacterDriver.IsEnabled = false;
        }
        SyncBasisTrackingOriginToKobold();

        if (!vrActive) {
            EnforceDesktopCameraOwnership();
            return;
        }

        EnforceVRCameraOwnership();
        if (Time.unscaledTime >= nextDeviceRefreshTime || leftInput == null || rightInput == null) {
            RefreshTrackedInputs();
            nextDeviceRefreshTime = Time.unscaledTime + 1f;
        }
        ProcessVRInput();
    }

    private void LateUpdate() {
        if (!bound || !vrActive) {
            return;
        }

        UpdateTrackedPoseProxies();
        UpdateKoboldHandIK();
    }

    private void OnBasisLocalPlayerInitialized() {
        TryBindBasisPlayer();
    }

    private void OnBasisLocalAvatarChanged() {
        if (bound) {
            HideBasisAvatarRenderers();
        }
    }

    private void OnBasisDeviceInitializationCompleted() {
        TryBindBasisPlayer();
        RefreshTrackedInputs();
    }

    private void OnBasisBootModeChanged(string mode) {
        RefreshTrackedInputs();
    }

    private void TryBindBasisPlayer() {
        if (bound || !BasisLocalPlayer.PlayerReady || BasisLocalPlayer.Instance == null) {
            return;
        }

        basisPlayer = BasisLocalPlayer.Instance;
        bound = true;
        HideBasisAvatarRenderers();
        if (basisPlayer.LocalCharacterDriver != null) {
            basisPlayer.LocalCharacterDriver.IsEnabled = false;
        }
        RefreshTrackedInputs();
        SetVRActive(BasisDeviceManagement.IsCurrentModeVR());
    }

    private void SetVRActive(bool active) {
        if (vrActive == active && possession != null && possession.IsBasisVRInputActive == active) {
            return;
        }

        vrActive = active;
        ResetButtonEdges();
        if (possession != null) {
            possession.SetBasisVRInputActive(active);
        }

        if (active) {
            CaptureKoboldCameraState();
            if (cameraSwitcher != null) {
                cameraSwitcherWasEnabled = cameraSwitcher.enabled;
                cameraSwitcher.enabled = false;
                if (cameraSwitcher.FPSCanvas != null) {
                    fpsCanvasWasActive = cameraSwitcher.FPSCanvas.activeSelf;
                    cameraSwitcher.FPSCanvas.SetActive(false);
                }
            }
            if (grabber != null) {
                grabber.SetView(rightAimProxy, true);
            }
            if (precisionGrabber != null) {
                precisionGrabber.SetView(rightAimProxy, true);
            }
            EnforceVRCameraOwnership();
        } else {
            if (grabber != null && defaultGrabView != null) {
                grabber.SetView(defaultGrabView, false);
            }
            if (precisionGrabber != null && defaultGrabView != null) {
                precisionGrabber.SetView(defaultGrabView, false);
            }
            if (handIK != null) {
                handIK.UnsetIKTarget(0);
                handIK.UnsetIKTarget(1);
            }
            RestoreKoboldCameraState();
            EnforceDesktopCameraOwnership();
        }
    }

    private void ProcessVRInput() {
        if (possession == null) {
            return;
        }

        if (Pauser.GetPaused()) {
            possession.ApplyBasisVRMovement(Vector2.zero, GetHeadRotation(), false, false);
            if (previousGrab) {
                possession.BasisVRGrabReleased();
            }
            if (previousActivate) {
                possession.BasisVRActivateReleased();
            }
            ResetButtonEdges();
            return;
        }

        BasisInputState leftState = leftInput != null ? leftInput.CurrentInputState : null;
        BasisInputState rightState = rightInput != null ? rightInput.CurrentInputState : null;

        Vector2 movement = leftState != null ? leftState.Primary2DAxisDeadZoned : Vector2.zero;
        bool jump = rightState != null && rightState.PrimaryButtonGetState;
        bool walk = leftState != null && leftState.Primary2DAxisClick;
        possession.ApplyBasisVRMovement(movement, GetHeadRotation(), jump, walk);

        // Basis controller mapping for the initial native VR pass:
        // right grip = grab, left grip = precision-grab modifier,
        // right trigger = activate held item, B/Y-secondary = use,
        // left secondary face button = unfreeze, A/X-primary = jump.
        bool grab = rightState != null && rightState.GripButton;
        bool precisionModifier = leftState != null && leftState.GripButton;
        bool activate = rightState != null && rightState.Trigger >= ButtonThreshold;
        bool use = rightState != null && rightState.SecondaryButtonGetState;
        bool unfreeze = leftState != null && leftState.SecondaryButtonGetState;

        if (grab && !previousGrab) {
            possession.BasisVRGrabPressed(precisionModifier);
        } else if (!grab && previousGrab) {
            possession.BasisVRGrabReleased();
        }

        if (activate && !previousActivate) {
            possession.BasisVRActivatePressed();
        } else if (!activate && previousActivate) {
            possession.BasisVRActivateReleased();
        }

        if (use && !previousUse) {
            possession.BasisVRUse();
        }
        if (unfreeze && !previousUnfreeze) {
            possession.BasisVRUnfreeze();
        }

        previousGrab = grab;
        previousActivate = activate;
        previousUse = use;
        previousUnfreeze = unfreeze;
    }

    private void RefreshTrackedInputs() {
        leftInput = null;
        rightInput = null;
        BasisDeviceManagement management = BasisDeviceManagement.Instance;
        if (management == null || management.AllInputDevices == null) {
            return;
        }

        for (int i = 0; i < management.AllInputDevices.Count; i++) {
            BasisInput input = management.AllInputDevices[i];
            if (input == null || !input.TryGetRole(out BasisBoneTrackedRole role)) {
                continue;
            }

            if (role == BasisBoneTrackedRole.LeftHand && leftInput == null) {
                leftInput = input;
            } else if (role == BasisBoneTrackedRole.RightHand && rightInput == null) {
                rightInput = input;
            }
        }
    }

    private void SyncBasisTrackingOriginToKobold() {
        if (basisPlayer == null || koboldController == null) {
            return;
        }

        Vector3 feetPosition = koboldController.transform.position;
        CapsuleCollider capsule = koboldController.collider;
        if (capsule != null) {
            feetPosition = capsule.transform.TransformPoint(
                capsule.center - Vector3.up * (capsule.height * 0.5f));
        }

        Quaternion yaw = Quaternion.Euler(0f, koboldController.transform.eulerAngles.y, 0f);
        Transform root = basisPlayer.PlayerSelf != null ? basisPlayer.PlayerSelf : basisPlayer.transform;
        root.SetPositionAndRotation(feetPosition, yaw);
        BasisLocalPlayer.localToWorldMatrix = Matrix4x4.TRS(feetPosition, yaw, root.lossyScale);
    }

    private void UpdateTrackedPoseProxies() {
        if (BasisLocalBoneDriver.LeftHandControl != null) {
            BasisCalibratedCoords pose = BasisLocalBoneDriver.LeftHandControl.OutgoingWorldData;
            leftHandProxy.SetPositionAndRotation(pose.position, pose.rotation);
        }
        if (BasisLocalBoneDriver.RightHandControl != null) {
            BasisCalibratedCoords pose = BasisLocalBoneDriver.RightHandControl.OutgoingWorldData;
            rightHandProxy.SetPositionAndRotation(pose.position, pose.rotation);
        }

        if (rightInput != null) {
            rightAimProxy.SetPositionAndRotation(
                rightInput.RaycastCoord.position,
                rightInput.RaycastCoord.rotation);
        } else if (BasisLocalBoneDriver.RightHandControl != null) {
            BasisCalibratedCoords pose = BasisLocalBoneDriver.RightHandControl.OutgoingWorldData;
            rightAimProxy.SetPositionAndRotation(pose.position, pose.rotation);
        }
    }

    private void UpdateKoboldHandIK() {
        if (handIK == null) {
            Animator model = characterAnimator != null ? characterAnimator.GetPlayerModel() : null;
            handIK = model != null ? model.GetComponent<HandIK>() : null;
            if (handIK == null) {
                return;
            }
        }

        if (BasisLocalBoneDriver.LeftHandControl != null) {
            handIK.SetIKTarget(0, leftHandProxy.position, leftHandProxy.rotation);
        }
        if (BasisLocalBoneDriver.RightHandControl != null) {
            handIK.SetIKTarget(1, rightHandProxy.position, rightHandProxy.rotation);
        }
    }

    private Quaternion GetHeadRotation() {
        if (BasisLocalBoneDriver.EyeControl != null) {
            return BasisLocalBoneDriver.EyeControl.OutgoingWorldData.rotation;
        }
        if (BasisLocalCameraDriver.CameraInstance != null) {
            return BasisLocalCameraDriver.CameraInstance.transform.rotation;
        }
        return koboldController != null ? koboldController.transform.rotation : Quaternion.identity;
    }

    private void HideBasisAvatarRenderers() {
        RestoreBasisAvatarRenderers();
        if (basisPlayer == null || basisPlayer.BasisAvatar == null || basisPlayer.BasisAvatar.Animator == null) {
            return;
        }

        Renderer[] renderers = basisPlayer.BasisAvatar.Animator.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++) {
            Renderer renderer = renderers[i];
            if (renderer == null) {
                continue;
            }
            hiddenBasisRenderers.Add(new RendererState(renderer, renderer.enabled));
            renderer.enabled = false;
        }
    }

    private void RestoreBasisAvatarRenderers() {
        for (int i = 0; i < hiddenBasisRenderers.Count; i++) {
            RendererState state = hiddenBasisRenderers[i];
            if (state.Renderer != null) {
                state.Renderer.enabled = state.Enabled;
            }
        }
        hiddenBasisRenderers.Clear();
    }

    private void CaptureKoboldCameraState() {
        if (koboldCamera != null) {
            return;
        }

        koboldCamera = OrbitCamera.GetCamera();
        if (koboldCamera == null) {
            return;
        }
        koboldCameraWasEnabled = koboldCamera.enabled;
        koboldAudioListener = koboldCamera.GetComponent<AudioListener>();
        if (koboldAudioListener != null) {
            koboldAudioListenerWasEnabled = koboldAudioListener.enabled;
        }
    }

    private void EnforceVRCameraOwnership() {
        CaptureKoboldCameraState();
        if (koboldCamera != null && koboldCamera != BasisLocalCameraDriver.CameraInstance) {
            koboldCamera.enabled = false;
        }
        if (koboldAudioListener != null) {
            koboldAudioListener.enabled = false;
        }
        if (BasisLocalCameraDriver.CameraInstance != null) {
            BasisLocalCameraDriver.CameraInstance.enabled = true;
            AudioListener listener = BasisLocalCameraDriver.CameraInstance.GetComponent<AudioListener>();
            if (listener != null) {
                listener.enabled = true;
            }
        }
    }

    private void EnforceDesktopCameraOwnership() {
        if (BasisLocalCameraDriver.CameraInstance != null && BasisLocalCameraDriver.CameraInstance != koboldCamera) {
            BasisLocalCameraDriver.CameraInstance.enabled = false;
            AudioListener listener = BasisLocalCameraDriver.CameraInstance.GetComponent<AudioListener>();
            if (listener != null) {
                listener.enabled = false;
            }
        }
    }

    private void RestoreKoboldCameraState() {
        if (cameraSwitcher != null) {
            cameraSwitcher.enabled = cameraSwitcherWasEnabled;
            if (cameraSwitcher.FPSCanvas != null) {
                cameraSwitcher.FPSCanvas.SetActive(fpsCanvasWasActive);
            }
        }
        if (koboldCamera != null) {
            koboldCamera.enabled = koboldCameraWasEnabled;
        }
        if (koboldAudioListener != null) {
            koboldAudioListener.enabled = koboldAudioListenerWasEnabled;
        }
        koboldCamera = null;
        koboldAudioListener = null;
    }

    private void ResetButtonEdges() {
        previousGrab = false;
        previousActivate = false;
        previousUse = false;
        previousUnfreeze = false;
    }

    private Transform CreateProxy(string proxyName) {
        GameObject proxy = new GameObject(proxyName);
        proxy.hideFlags = HideFlags.DontSave;
        proxy.transform.SetParent(transform, false);
        return proxy.transform;
    }
}
