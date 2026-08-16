using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using KoboldKare.Basis.Networking;
using NetStack.Serialization;
using Photon.Pun;
using Photon.Realtime;
using SimpleJSON;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

[CreateAssetMenu(fileName = "NewNetworkManager", menuName = "Data/NetworkManager", order = 1)]
public class NetworkManager : SingletonScriptableObject<NetworkManager>, IPunOwnershipCallbacks {
    private string selectedMap;
    public PrefabSelectSingleSetting selectedPlayerPrefab;

    private bool cheatsEnabled;
    private bool basisSessionTransitionRunning;
    private uint appliedBasisSessionRevision;

    public bool online => !PhotonNetwork.OfflineMode &&
                          Basis.Scripts.Networking.BasisNetworkConnection.LocalPlayerIsConnected;
    public bool offline => !online;

    public void JoinLobby(string ignoredRegion) {
        // Existing menu events still call this method. Basis directories expose endpoints rather
        // than Photon regions/lobbies, so opening multiplayer simply refreshes the server browser.
        BasisServerBrowserRefresh.RequestRefresh();
    }

    public void LeaveLobby() {
        // Kept for serialized UI compatibility. There is no Basis lobby connection to leave.
    }

    public void QuickMatch() {
        if (GameManager.instance != null) {
            GameManager.instance.StartCoroutine(QuickMatchRoutine());
        }
    }

    private IEnumerator QuickMatchRoutine() {
        Popup popup = PopupHandler.instance?.SpawnPopup("Connect");
        Task<IReadOnlyList<KoboldKareServerEntry>> queryTask = KoboldKareServerDirectory.QueryAsync();
        yield return new WaitUntil(() => queryTask.IsCompleted);

        if (popup != null) {
            PopupHandler.instance?.ClearPopup(popup);
        }

        if (queryTask.IsFaulted) {
            string message = queryTask.Exception?.GetBaseException().Message ?? "Basis server discovery failed.";
            PopupHandler.instance?.SpawnPopup("Disconnect", true, default, message);
            yield break;
        }
        if (queryTask.IsCanceled || queryTask.Result.Count == 0) {
            PopupHandler.instance?.SpawnPopup(
                "Disconnect",
                true,
                default,
                "No KoboldKare Basis servers are currently available.");
            yield break;
        }

        KoboldKareServerEntry server = queryTask.Result[0];
        yield return JoinBasisServer(server.Endpoint);
    }

    public void SetSelectedMap(string mapName) {
        selectedMap = mapName;
    }

    public string GetSelectedMap() {
        return selectedMap;
    }

    public string GetApplicationVersion() {
        string version = Application.version;
        if (ModManager.GetModsWithLoadedAssets().Count != 0) {
            version += "modded";
        }
        return version;
    }

    public string BuildCurrentModListJson() {
        JSONArray modArray = new JSONArray();
        foreach (var mod in ModManager.GetModsWithLoadedAssets()) {
            JSONNode modNode = JSONNode.Parse("{}");
            modNode["title"] = mod.title;
            modNode["folderTitle"] = mod.folderTitle;
            modNode["id"] = mod.id.ToString();
            modArray.Add(modNode);
        }
        return modArray.ToString();
    }

    private bool TryParseMods(string modList, out List<ModManager.ModStub> stubs) {
        try {
            JSONNode modArray = JSONNode.Parse(string.IsNullOrWhiteSpace(modList) ? "[]" : modList);
            List<ModManager.ModStub> modsToLoad = new List<ModManager.ModStub>();
            foreach (var pair in modArray) {
                JSONNode node = pair.Value;
                if (!node.HasKey("id") || !node.HasKey("folderTitle") || !node.HasKey("title")) {
                    stubs = new List<ModManager.ModStub>();
                    return false;
                }

                if (!ulong.TryParse(node["id"], out ulong parsedID)) {
                    continue;
                }

                modsToLoad.Add(new ModManager.ModStub(
                    (string)node["title"],
                    new PublishedFileId_t(parsedID),
                    ModManager.ModSource.Any,
                    node["folderTitle"]));
            }
            stubs = modsToLoad;
            return true;
        } catch (Exception exception) {
            Debug.LogError($"Failed to parse KoboldKare Basis mod list: {exception}");
            stubs = new List<ModManager.ModStub>();
            return false;
        }
    }

    public IEnumerator HostBasisMatch(
        string serverName,
        int maxPlayers,
        bool privateRoom,
        string password = "") {
        if (string.IsNullOrWhiteSpace(selectedMap)) {
            PopupHandler.instance?.SpawnPopup(
                "Disconnect",
                true,
                default,
                "No KoboldKare map is selected for the Basis server.");
            yield break;
        }

        yield return PrepareForBasisConnectionSwitch();
        PhotonNetwork.OfflineMode = false;
        BasisSessionEventRelay.EnsureCreated();
        Popup popup = PopupHandler.instance?.SpawnPopup("Connect");
        MainMenu.ShowMenuStatic(MainMenu.MainMenuMode.Loading);

        Exception failure = null;
        KoboldKareConnectionOptions options = new KoboldKareConnectionOptions(
            "localhost",
            KoboldKareConnectionService.DefaultPort,
            password ?? string.Empty,
            ResolveDisplayName(),
            true,
            string.IsNullOrWhiteSpace(serverName) ? "KoboldKare" : serverName.Trim(),
            Mathf.Clamp(maxPlayers, 1, ushort.MaxValue));

        Task connectTask = null;
        try {
            connectTask = KoboldKareConnectionService.ConnectAsync(options);
        } catch (Exception exception) {
            failure = exception;
        }

        if (failure == null) {
            yield return new WaitUntil(() => connectTask.IsCompleted);
            try {
                ThrowIfTaskFailed(connectTask, "Basis server connection failed.");
            } catch (Exception exception) {
                failure = exception;
            }
        }

        if (failure == null) {
            float deadline = Time.realtimeSinceStartup + 10f;
            yield return new WaitUntil(() => {
                KoboldKareNetworkWorld world = KoboldKareNetworkWorld.Instance;
                KoboldKareSessionCoordinator session = KoboldKareSessionCoordinator.Instance;
                return (world != null && session != null &&
                        world.HasNetworkID && world.IsWorldAuthority && session.HasNetworkID) ||
                       Time.realtimeSinceStartup >= deadline;
            });

            try {
                KoboldKareNetworkWorld readyWorld = KoboldKareNetworkWorld.Instance;
                KoboldKareSessionCoordinator readySession = KoboldKareSessionCoordinator.Instance;
                if (readyWorld == null || readySession == null ||
                    !readyWorld.HasNetworkID || !readyWorld.IsWorldAuthority || !readySession.HasNetworkID) {
                    throw new TimeoutException("KoboldKare Basis session objects did not become network-ready.");
                }

                // Directory publication/private visibility is a server-directory concern, not gameplay
                // authority state. Keep the UI option accepted while provider-side publication is wired.
                _ = privateRoom;

                if (!readySession.SetSessionState(
                        selectedMap,
                        BuildCurrentModListJson(),
                        cheatsEnabled)) {
                    throw new InvalidOperationException("Failed to publish KoboldKare Basis session state.");
                }
            } catch (Exception exception) {
                failure = exception;
            }
        }

        if (failure != null) {
            Debug.LogError($"Failed to host KoboldKare Basis match: {failure}");
            PopupHandler.instance?.SpawnPopup(
                "Disconnect",
                true,
                default,
                failure.GetBaseException().Message);
        }
        if (popup != null) {
            PopupHandler.instance?.ClearPopup(popup);
        }
        MainMenu.ShowMenuStatic(MainMenu.MainMenuMode.None);
    }

    public IEnumerator JoinBasisServer(string endpoint, string password = "") {
        if (!TryParseBasisEndpoint(endpoint, out string address, out ushort port)) {
            PopupHandler.instance?.SpawnPopup(
                "Disconnect",
                true,
                default,
                $"Invalid Basis server endpoint: {endpoint}");
            yield break;
        }

        yield return PrepareForBasisConnectionSwitch();
        PhotonNetwork.OfflineMode = false;
        BasisSessionEventRelay.EnsureCreated();
        Popup popup = PopupHandler.instance?.SpawnPopup("Connect");
        MainMenu.ShowMenuStatic(MainMenu.MainMenuMode.Loading);
        Exception failure = null;
        KoboldKareConnectionOptions options = new KoboldKareConnectionOptions(
            address,
            port,
            password ?? string.Empty,
            ResolveDisplayName());
        Task connectTask = null;
        try {
            connectTask = KoboldKareConnectionService.ConnectAsync(options);
        } catch (Exception exception) {
            failure = exception;
        }

        if (failure == null) {
            yield return new WaitUntil(() => connectTask.IsCompleted);
            try {
                ThrowIfTaskFailed(connectTask, "Basis server connection failed.");
            } catch (Exception exception) {
                failure = exception;
            }
        }

        if (failure == null) {
            float deadline = Time.realtimeSinceStartup + 10f;
            yield return new WaitUntil(() =>
                (KoboldKareSessionCoordinator.Instance != null &&
                 KoboldKareSessionCoordinator.Instance.HasNetworkID &&
                 KoboldKareSessionCoordinator.Instance.HasState) ||
                Time.realtimeSinceStartup >= deadline);

            if (KoboldKareSessionCoordinator.Instance == null ||
                !KoboldKareSessionCoordinator.Instance.HasState) {
                failure = new TimeoutException("The Basis server did not provide KoboldKare session state.");
            }
        }

        if (failure != null) {
            Debug.LogError($"Failed to join KoboldKare Basis server '{endpoint}': {failure}");
            PopupHandler.instance?.SpawnPopup(
                "Disconnect",
                true,
                default,
                failure.GetBaseException().Message);
        }
        if (popup != null) {
            PopupHandler.instance?.ClearPopup(popup);
        }
        MainMenu.ShowMenuStatic(MainMenu.MainMenuMode.None);
    }

    public void StartSinglePlayer() {
        if (GameManager.instance != null) {
            GameManager.instance.StartCoroutine(SinglePlayerRoutine());
        }
    }

    public IEnumerator SinglePlayerRoutine() {
        yield return PrepareForBasisConnectionSwitch();
        PhotonNetwork.OfflineMode = true;
        BasisSessionEventRelay.EnsureCreated();

        MainMenu.ShowMenuStatic(MainMenu.MainMenuMode.Loading);
        if (!string.IsNullOrWhiteSpace(selectedMap) && SceneManager.GetActiveScene().name != selectedMap) {
            var mapHandle = MapLoadingInterop.RequestMapLoad(selectedMap);
            yield return new WaitUntil(() => mapHandle.IsDone);
        }

        PopupHandler.instance?.ClearAllPopups();
        SpawnControllablePlayer();
        MainMenu.ShowMenuStatic(MainMenu.MainMenuMode.None);
    }

    public IEnumerator SpawnControllablePlayerRoutine() {
        yield return new WaitUntil(ModManager.GetFinishedLoading);

        if (!PhotonNetwork.OfflineMode) {
            float deadline = Time.realtimeSinceStartup + 10f;
            yield return new WaitUntil(() => {
                KoboldKareNetworkWorld world = KoboldKareNetworkWorld.Instance;
                return (Basis.Scripts.Networking.BasisNetworkConnection.LocalPlayerIsConnected &&
                        world != null && world.HasNetworkID) ||
                       Time.realtimeSinceStartup >= deadline;
            });

            if (!Basis.Scripts.Networking.BasisNetworkConnection.LocalPlayerIsConnected ||
                KoboldKareNetworkWorld.Instance == null ||
                !KoboldKareNetworkWorld.Instance.HasNetworkID) {
                Debug.LogError("Cannot spawn the controllable Kobold because the Basis session is not network-ready.");
                yield break;
            }
        }

        if (PhotonNetwork.LocalPlayer.TagObject is Kobold) {
            yield break;
        }

        BitBuffer playerData = new BitBuffer(16);
        playerData.AddKoboldGenes(PlayerKoboldLoader.GetPlayerGenes());
        playerData.AddBool(true);

        SceneDescriptor.GetSpawnLocationAndRotation(out Vector3 position, out Quaternion rotation);
        Debug.Log($"Spawned player at {position}");
        GameObject player = PhotonNetwork.Instantiate(
            selectedPlayerPrefab.GetPrefab(),
            position,
            Quaternion.identity,
            0,
            new object[] { playerData });
        if (player == null) {
            Debug.LogError("Basis-backed player instantiation failed.");
            yield break;
        }

        CharacterDescriptor descriptor = player.GetComponentInChildren<CharacterDescriptor>(true);
        if (descriptor != null) {
            descriptor.SetEyeDir(rotation * Vector3.forward);
        }
        PopupHandler.instance?.ClearAllPopups();
        MainMenu.ShowMenuStatic(MainMenu.MainMenuMode.None);
        Pauser.SetPaused(false);
    }

    public void SpawnControllablePlayer() {
        if (GameManager.instance != null) {
            GameManager.instance.StartCoroutine(SpawnControllablePlayerRoutine());
        }
    }

    public void TriggerDisconnect() {
        if (GameManager.instance != null) {
            GameManager.instance.StartCoroutine(DisconnectBasisRoutine());
        }
    }

    public IEnumerator DisconnectForSceneChange() {
        PhotonNetwork.OfflineMode = false;
        bool hasBasisRuntime =
            Basis.Scripts.Networking.BasisNetworkManagement.IsInitialized ||
            Basis.Scripts.Networking.BasisNetworkConnection.LocalPlayerPeer != null;

        if (hasBasisRuntime && SceneManager.GetActiveScene().name != "ErrorScene") {
            var unloadHandle = MapLoadingInterop.RequestMapLoad("ErrorScene");
            yield return new WaitUntil(() => unloadHandle.IsDone);
            yield return null;
        }

        if (hasBasisRuntime) {
            Task disconnectTask = KoboldKareConnectionService.DisconnectAsync();
            yield return new WaitUntil(() => disconnectTask.IsCompleted);
            if (disconnectTask.IsFaulted) {
                Debug.LogError($"Basis disconnect failed: {disconnectTask.Exception}");
            }
        }

        appliedBasisSessionRevision = 0;
        cheatsEnabled = false;
        PopupHandler.instance?.ClearAllPopups();
    }

    private IEnumerator DisconnectBasisRoutine() {
        yield return DisconnectForSceneChange();
        MainMenu.ShowMenuStatic(MainMenu.MainMenuMode.MainMenu);
    }

    public void OnOwnershipRequest(PhotonView targetView, Player requestingPlayer) {
        if (targetView == null || requestingPlayer == null) {
            return;
        }

        Kobold kobold = targetView.GetComponent<Kobold>();
        if (kobold != (Kobold)PhotonNetwork.LocalPlayer.TagObject) {
            targetView.TransferOwnership(requestingPlayer);
            return;
        }

        bool denyTemporarySteal =
            PlayerPossession.TryGetPlayerInstance(out PlayerPossession possession) &&
            possession.kobold == kobold &&
            !possession.IsBasisVRInputActive &&
            GameManager.GetPlayerControls().Player.Jump.IsPressed();
        if (!denyTemporarySteal || requestingPlayer == PhotonNetwork.LocalPlayer) {
            targetView.TransferOwnership(requestingPlayer);
        }
    }

    public void OnOwnershipTransfered(PhotonView targetView, Player previousOwner) {
    }

    public void OnOwnershipTransferFailed(PhotonView targetView, Player senderOfFailedRequest) {
    }

    public bool GetCheatsEnabled() => cheatsEnabled;

    public bool SendChat(string message) {
        if (string.IsNullOrWhiteSpace(message)) {
            return false;
        }
        message = message.TrimEnd();

        KoboldKareSessionCoordinator coordinator = KoboldKareSessionCoordinator.Instance;
        if (coordinator != null && !PhotonNetwork.OfflineMode) {
            return coordinator.SendChat(message);
        }

        Player player = PhotonNetwork.LocalPlayer;
        string displayName = player != null ? player.NickName : "Player";
        CheatsProcessor.AppendText($"{displayName}: {message}\n");
        Kobold kobold = player?.TagObject as Kobold;
        if (kobold != null) {
            Chatter chatter = kobold.GetComponent<Chatter>();
            if (chatter != null) {
                chatter.DisplayMessage(message, 1f);
            }
            CheatsProcessor.ProcessCommand(kobold, message);
        }
        return true;
    }

    public bool SetCheatsEnabled(bool enabled) {
        KoboldKareSessionCoordinator coordinator = KoboldKareSessionCoordinator.Instance;
        if (coordinator == null || PhotonNetwork.OfflineMode) {
            if (!PhotonNetwork.OfflineMode && PhotonNetwork.IsConnected) {
                return false;
            }
            cheatsEnabled = enabled;
            return true;
        }
        return coordinator.SetCheatsEnabled(enabled);
    }

    public void ApplyBasisSessionState(KoboldKareSessionState state) {
        if (PhotonNetwork.OfflineMode) {
            return;
        }
        if (state.Revision != 0 && appliedBasisSessionRevision != 0 &&
            !IsRevisionNewer(state.Revision, appliedBasisSessionRevision)) {
            return;
        }

        cheatsEnabled = state.CheatsEnabled;
        selectedMap = state.MapName;
        if (state.Revision != 0) {
            appliedBasisSessionRevision = state.Revision;
        }

        if (!TryParseMods(state.ModListJson, out List<ModManager.ModStub> stubs)) {
            Debug.LogError("Basis KoboldKare session supplied an invalid mod list.");
            PopupHandler.instance?.SpawnPopup(
                "Disconnect",
                true,
                default,
                "Server supplied an invalid mod list.");
            return;
        }

        if (basisSessionTransitionRunning || GameManager.instance == null) {
            return;
        }
        bool needsMods = !HasExactModConfigurationLoaded(stubs);
        bool needsMap = !string.IsNullOrWhiteSpace(state.MapName) &&
                        SceneManager.GetActiveScene().name != state.MapName;
        bool needsPlayer = PhotonNetwork.LocalPlayer?.TagObject is not Kobold;
        if (!needsMods && !needsMap && !needsPlayer) {
            return;
        }

        GameManager.instance.StartCoroutine(ApplyBasisSessionStateRoutine(state, stubs));
    }

    private IEnumerator ApplyBasisSessionStateRoutine(
        KoboldKareSessionState state,
        List<ModManager.ModStub> modsToLoad) {
        if (basisSessionTransitionRunning) {
            yield break;
        }

        basisSessionTransitionRunning = true;
        try {
            MainMenu.ShowMenuStatic(MainMenu.MainMenuMode.Loading);
            PopupHandler.instance?.SpawnPopup("Connect");

            if (!HasExactModConfigurationLoaded(modsToLoad)) {
                if (SceneManager.GetActiveScene().name != "ErrorScene") {
                    var unloadHandle = MapLoadingInterop.RequestMapLoad("ErrorScene");
                    yield return new WaitUntil(() => unloadHandle.IsDone);
                }

                yield return GameManager.instance.StartCoroutine(ModManager.SetLoadedMods(modsToLoad));
                if (ModManager.GetFailedToLoadMods()) {
                    MainMenu.ShowMenuStatic(MainMenu.MainMenuMode.MainMenu);
                    PopupHandler.instance?.ClearAllPopups();
                    PopupHandler.instance?.SpawnPopup(
                        "Disconnect",
                        true,
                        default,
                        "Failed to download mods set by the Basis server.");
                    yield break;
                }
            }

            if (!string.IsNullOrWhiteSpace(state.MapName) &&
                SceneManager.GetActiveScene().name != state.MapName) {
                var mapHandle = MapLoadingInterop.RequestMapLoad(state.MapName);
                yield return new WaitUntil(() => mapHandle.IsDone);
            }

            PopupHandler.instance?.ClearAllPopups();
            SpawnControllablePlayer();
        } finally {
            basisSessionTransitionRunning = false;
        }
    }

    private IEnumerator PrepareForBasisConnectionSwitch() {
        bool hasBasisConnection =
            Basis.Scripts.Networking.BasisNetworkConnection.LocalPlayerIsConnected ||
            Basis.Scripts.Networking.BasisNetworkConnection.LocalPlayerPeer != null;
        bool shouldResetRuntime = hasBasisConnection || PhotonNetwork.OfflineMode;
        if (!shouldResetRuntime) {
            yield break;
        }

        if (SceneManager.GetActiveScene().name != "ErrorScene") {
            var unloadHandle = MapLoadingInterop.RequestMapLoad("ErrorScene");
            yield return new WaitUntil(() => unloadHandle.IsDone);
            yield return null;
        }

        if (Basis.Scripts.Networking.BasisNetworkManagement.IsInitialized || hasBasisConnection) {
            Task resetTask = KoboldKareConnectionService.DisconnectAsync();
            yield return new WaitUntil(() => resetTask.IsCompleted);
            if (resetTask.IsFaulted) {
                throw resetTask.Exception?.GetBaseException() ??
                      new InvalidOperationException("Basis runtime reset failed.");
            }
            if (resetTask.IsCanceled) {
                throw new OperationCanceledException("Basis runtime reset was cancelled.");
            }
        }

        PhotonNetwork.OfflineMode = false;
        appliedBasisSessionRevision = 0;
    }

    private static bool HasExactModConfigurationLoaded(IList<ModManager.ModStub> requested) {
        List<ModManager.ModStub> loaded = ModManager.GetModsWithLoadedAssets();
        if (loaded.Count != requested.Count) {
            return false;
        }

        for (int i = 0; i < requested.Count; i++) {
            bool found = false;
            for (int j = 0; j < loaded.Count; j++) {
                if (requested[i].GetRepresentedBy(loaded[j])) {
                    found = true;
                    break;
                }
            }
            if (!found) {
                return false;
            }
        }
        return true;
    }

    private static bool TryParseBasisEndpoint(string endpoint, out string address, out ushort port) {
        address = string.Empty;
        port = KoboldKareConnectionService.DefaultPort;
        if (string.IsNullOrWhiteSpace(endpoint)) {
            return false;
        }

        string value = endpoint.Trim();
        if (value[0] == '[') {
            int closingBracket = value.IndexOf(']');
            if (closingBracket <= 1) {
                return false;
            }
            address = value.Substring(1, closingBracket - 1);
            if (closingBracket + 1 == value.Length) {
                return true;
            }
            if (value[closingBracket + 1] != ':' ||
                !ushort.TryParse(value.Substring(closingBracket + 2), out port)) {
                return false;
            }
            return true;
        }

        int firstColon = value.IndexOf(':');
        int lastColon = value.LastIndexOf(':');
        if (firstColon >= 0 && firstColon == lastColon) {
            address = value.Substring(0, firstColon);
            return !string.IsNullOrWhiteSpace(address) &&
                   ushort.TryParse(value.Substring(firstColon + 1), out port);
        }

        // Unbracketed IPv6 is accepted using the default KoboldKare/Basis port.
        address = value;
        return true;
    }

    private static void ThrowIfTaskFailed(Task task, string fallbackMessage) {
        if (task.IsFaulted) {
            throw task.Exception?.GetBaseException() ?? new InvalidOperationException(fallbackMessage);
        }
        if (task.IsCanceled) {
            throw new OperationCanceledException(fallbackMessage);
        }
    }

    private static string ResolveDisplayName() {
        if (!string.IsNullOrWhiteSpace(SettingNickname.CurrentNickname)) {
            return SettingNickname.CurrentNickname;
        }
        BasisLocalPlayerNameResolver.TryResolve(out string displayName);
        return string.IsNullOrWhiteSpace(displayName) ? SystemInfo.deviceName : displayName;
    }

    private static bool IsRevisionNewer(uint revision, uint previous) {
        return revision != previous && unchecked(revision - previous) < 0x80000000U;
    }
}

internal static class BasisLocalPlayerNameResolver {
    public static bool TryResolve(out string displayName) {
        displayName = null;
        Basis.Scripts.BasisSdk.Players.BasisLocalPlayer player =
            Basis.Scripts.BasisSdk.Players.BasisLocalPlayer.Instance;
        if (player == null || string.IsNullOrWhiteSpace(player.DisplayName)) {
            return false;
        }
        displayName = player.DisplayName;
        return true;
    }
}
