using Basis.Scripts.Networking;
using TMPro;
using UnityEngine;

[RequireComponent(typeof(TMP_Text))]
public class ServerNameLabel : MonoBehaviour {
    private TMP_Text label;

    private void Awake() {
        label = GetComponent<TMP_Text>();
    }

    private void Update() {
        if (!BasisNetworkConnection.LocalPlayerIsConnected) {
            label.text = string.Empty;
            return;
        }

        if (BasisNetworkManagement.IsHostMode) {
            label.text = string.IsNullOrWhiteSpace(BasisNetworkManagement.HostServerName)
                ? "KoboldKare"
                : BasisNetworkManagement.HostServerName;
        } else {
            label.text = $"{BasisNetworkManagement.Ip}:{BasisNetworkManagement.Port}";
        }
    }
}
