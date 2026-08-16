using System.Linq;
using KoboldKare.Basis.Networking;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Bridges Basis session events into KoboldKare's existing UI/gameplay-facing NetworkManager.
/// It is created automatically so the first port does not require touching every map prefab.
/// </summary>
public sealed class BasisSessionEventRelay : MonoBehaviour {
    private static BasisSessionEventRelay instance;
    private KoboldKareSessionCoordinator coordinator;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InitializeRuntimeHooks() {
        KoboldKareConnectionService.NetworkRuntimeTeardownRequested -= TeardownRuntime;
        KoboldKareConnectionService.NetworkRuntimeTeardownRequested += TeardownRuntime;
        KoboldKareConnectionService.NetworkRuntimeReinitializeRequested -= EnsureCreated;
        KoboldKareConnectionService.NetworkRuntimeReinitializeRequested += EnsureCreated;
        EnsureCreated();
    }

    public static void EnsureCreated() {
        if (instance != null) return;
        var gameObject = new GameObject("KoboldKare Basis Network Runtime");
        DontDestroyOnLoad(gameObject);
        gameObject.AddComponent<KoboldKareNetworkWorld>();
        gameObject.AddComponent<KoboldKareSessionCoordinator>();
        instance = gameObject.AddComponent<BasisSessionEventRelay>();
    }

    private static void TeardownRuntime() {
        if (instance == null) return;
        GameObject runtimeObject = instance.gameObject;
        instance = null;
        // Basis lifecycle reset clears callback tables synchronously. Remove the old behaviours
        // synchronously as well so singleton guards do not block recreation before Connect().
        DestroyImmediate(runtimeObject);
    }

    private void Update() {
        if (coordinator == KoboldKareSessionCoordinator.Instance) return;
        Unbind();
        Bind(KoboldKareSessionCoordinator.Instance);
    }

    private void OnDestroy() {
        Unbind();
        if (instance == this) instance = null;
    }

    private void Bind(KoboldKareSessionCoordinator next) {
        coordinator = next;
        if (NetworkManager.instance != null) {
            PhotonNetwork.AddCallbackTarget(NetworkManager.instance);
        }
        if (coordinator == null) return;

        coordinator.StateChanged += OnStateChanged;
        coordinator.ChatReceived += OnChatReceived;
        coordinator.PlayerJoined += OnPlayerJoined;
        coordinator.PlayerLeft += OnPlayerLeft;
        coordinator.WorldAuthorityChanged += OnWorldAuthorityChanged;
        coordinator.Kicked += OnKicked;

        if (coordinator.HasState) {
            OnStateChanged(coordinator.State);
        }
    }

    private void Unbind() {
        if (NetworkManager.instance != null) {
            PhotonNetwork.RemoveCallbackTarget(NetworkManager.instance);
        }
        if (coordinator == null) return;
        coordinator.StateChanged -= OnStateChanged;
        coordinator.ChatReceived -= OnChatReceived;
        coordinator.PlayerJoined -= OnPlayerJoined;
        coordinator.PlayerLeft -= OnPlayerLeft;
        coordinator.WorldAuthorityChanged -= OnWorldAuthorityChanged;
        coordinator.Kicked -= OnKicked;
        coordinator = null;
    }

    private static void OnStateChanged(KoboldKareSessionState state) {
        NetworkManager.instance?.ApplyBasisSessionState(state);
    }

    private static void OnChatReceived(ushort playerId, string message) {
        Player player = PhotonNetwork.PlayerList.FirstOrDefault(candidate => candidate.ActorNumber == playerId);
        string displayName = player != null ? player.NickName : $"Player {playerId}";
        CheatsProcessor.AppendText($"{displayName}: {message}\n");

        Kobold chatKobold = player?.TagObject as Kobold;
        if (chatKobold == null) return;

        Chatter chatter = chatKobold.GetComponent<Chatter>();
        if (chatter != null) {
            chatter.DisplayMessage(message, 1f);
        }

        if (player == PhotonNetwork.LocalPlayer) {
            CheatsProcessor.ProcessCommand(chatKobold, message);
        }
    }

    private static void OnPlayerJoined(ushort playerId, string displayName) {
        CheatsProcessor.AppendText($"{displayName}<color=yellow> has joined the room.</color>\n");
    }

    private static void OnPlayerLeft(ushort playerId, string displayName) {
        CheatsProcessor.AppendText($"{displayName}<color=yellow> has left the room.</color>\n");
    }

    private static void OnWorldAuthorityChanged(bool isLocalAuthority) {
        if (!isLocalAuthority) return;
        CheatsProcessor.AppendText("<color=yellow>Host migrated to this client.</color>\n");
    }

    private static void OnKicked(string reason) {
        CheatsProcessor.AppendText($"<color=yellow>Disconnected by host: {reason}</color>\n");
        PopupHandler.instance?.SpawnPopup("Disconnect", true, default, reason);
        if (SceneManager.GetActiveScene().name != "ErrorScene") {
            SceneManager.LoadScene("ErrorScene", LoadSceneMode.Single);
        }
        MainMenu.ShowMenuStatic(MainMenu.MainMenuMode.MainMenu);
    }
}
