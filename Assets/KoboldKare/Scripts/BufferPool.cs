using UnityEngine;

/// <summary>
/// Retained as a serialized scene component for compatibility with existing KoboldKare content.
/// Photon custom-type registration is no longer required: the Basis-backed compatibility codec
/// serializes NetStack BitBuffer payloads directly.
/// </summary>
public class BufferPool : MonoBehaviour {
    private static BufferPool instance;

    private void Awake() {
        if (instance != null && instance != this) {
            Destroy(this);
            return;
        }
        instance = this;
    }
}
