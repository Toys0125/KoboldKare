using Basis.Scripts.BasisSdk.Players;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Keeps legacy multiplayer buttons disabled until the Basis local-player runtime is ready.
/// Photon lobby callbacks are no longer part of KoboldKare's connection lifecycle.
/// </summary>
public class DisableUntilNetworkReady : MonoBehaviour {
    private Selectable selectable;

    private void Awake() {
        selectable = GetComponent<Selectable>();
    }

    private void OnEnable() {
        BasisLocalPlayer.OnLocalPlayerInitialized += Refresh;
        Refresh();
    }

    private void OnDisable() {
        BasisLocalPlayer.OnLocalPlayerInitialized -= Refresh;
    }

    private void Refresh() {
        if (selectable != null) {
            selectable.interactable = BasisLocalPlayer.PlayerReady;
        }
    }
}
