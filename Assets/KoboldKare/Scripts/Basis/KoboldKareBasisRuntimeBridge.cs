using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using UnityEngine;

/// <summary>
/// Application-level ownership shim for Basis' local runtime. KoboldKare owns locomotion and its
/// visible player body, while Basis owns device discovery, tracking and the native XR camera.
/// </summary>
[DefaultExecutionOrder(-1000)]
public sealed class KoboldKareBasisRuntimeBridge : MonoBehaviour {
    private static KoboldKareBasisRuntimeBridge instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void EnsureCreated() {
        if (instance != null) {
            return;
        }
        GameObject root = new GameObject("KoboldKare Basis Runtime Bridge");
        DontDestroyOnLoad(root);
        instance = root.AddComponent<KoboldKareBasisRuntimeBridge>();
    }

    private void OnEnable() {
        BasisLocalPlayer.OnLocalPlayerInitialized += ApplyLocalPlayerPolicy;
        BasisLocalPlayer.OnLocalAvatarChanged += HideBasisAvatar;
        ApplyLocalPlayerPolicy();
    }

    private void OnDisable() {
        BasisLocalPlayer.OnLocalPlayerInitialized -= ApplyLocalPlayerPolicy;
        BasisLocalPlayer.OnLocalAvatarChanged -= HideBasisAvatar;
    }

    private void Update() {
        ApplyLocalPlayerPolicy();

        if (PlayerPossession.TryGetPlayerInstance(out PlayerPossession possession) &&
            possession != null && possession.isActiveAndEnabled) {
            // The possession-level bridge owns camera switching while a Kobold is controlled.
            return;
        }

        if (!BasisDeviceManagement.IsCurrentModeVR()) {
            SetBasisCameraEnabled(false);
        } else {
            // In menus/loading scenes Basis remains the native XR camera. Screen-space overlay UI
            // continues to render while there is no possessed Kobold camera bridge yet.
            SetBasisCameraEnabled(true);
        }
    }

    private static void ApplyLocalPlayerPolicy() {
        BasisLocalPlayer player = BasisLocalPlayer.Instance;
        if (player == null) {
            return;
        }

        if (player.LocalCharacterDriver != null) {
            player.LocalCharacterDriver.IsEnabled = false;
        }
        HideBasisAvatar();
    }

    private static void HideBasisAvatar() {
        BasisLocalPlayer player = BasisLocalPlayer.Instance;
        if (player == null || player.BasisAvatar == null || player.BasisAvatar.Animator == null) {
            return;
        }

        Renderer[] renderers = player.BasisAvatar.Animator.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++) {
            if (renderers[i] != null) {
                renderers[i].enabled = false;
            }
        }
    }

    private static void SetBasisCameraEnabled(bool enabled) {
        Camera camera = BasisLocalCameraDriver.CameraInstance;
        if (camera == null) {
            return;
        }
        camera.enabled = enabled;
        AudioListener listener = camera.GetComponent<AudioListener>();
        if (listener != null) {
            listener.enabled = enabled;
        }
    }
}
