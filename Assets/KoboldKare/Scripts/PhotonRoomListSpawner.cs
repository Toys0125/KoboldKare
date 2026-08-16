using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KoboldKare.Basis.Networking;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Compatibility-named server browser for the existing KoboldKare multiplayer UI.
/// Photon room discovery has been replaced with Basis server-directory discovery.
/// </summary>
public sealed class PhotonRoomListSpawner : MonoBehaviour {
    public GameObject roomPrefab;
    public GameObject hideOnRoomsFound;
    [SerializeField] private NetworkManager networkManager;
    [SerializeField, Min(1f)] private float refreshIntervalSeconds = 5f;

    private readonly List<GameObject> roomPrefabs = new List<GameObject>();
    private CancellationTokenSource refreshCancellation;
    private Coroutine refreshRoutine;
    private static string[] blacklist;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Init() {
        blacklist = null;
    }

    private void OnEnable() {
        BasisServerBrowserRefresh.RefreshRequested += RequestImmediateRefresh;
        refreshCancellation = new CancellationTokenSource();
        refreshRoutine = StartCoroutine(RefreshLoop());
    }

    private void OnDisable() {
        BasisServerBrowserRefresh.RefreshRequested -= RequestImmediateRefresh;
        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
        refreshCancellation = null;
        if (refreshRoutine != null) {
            StopCoroutine(refreshRoutine);
            refreshRoutine = null;
        }
        ClearRoomList();
    }

    private void RequestImmediateRefresh() {
        if (!isActiveAndEnabled) {
            return;
        }
        if (refreshRoutine != null) {
            StopCoroutine(refreshRoutine);
        }
        refreshRoutine = StartCoroutine(RefreshLoop());
    }

    private IEnumerator RefreshLoop() {
        while (isActiveAndEnabled) {
            CancellationToken token = refreshCancellation?.Token ?? CancellationToken.None;
            Task<IReadOnlyList<KoboldKareServerEntry>> queryTask = KoboldKareServerDirectory.QueryAsync(token);
            yield return new WaitUntil(() => queryTask.IsCompleted);

            if (queryTask.IsCanceled || token.IsCancellationRequested) {
                yield break;
            }
            if (queryTask.IsFaulted) {
                Debug.LogWarning($"Basis server-directory refresh failed: {queryTask.Exception?.GetBaseException().Message}");
                ApplyServers(Array.Empty<KoboldKareServerEntry>());
            } else {
                ApplyServers(queryTask.Result);
            }

            float deadline = Time.unscaledTime + Mathf.Max(1f, refreshIntervalSeconds);
            while (isActiveAndEnabled && Time.unscaledTime < deadline) {
                yield return null;
            }
        }
        refreshRoutine = null;
    }

    private void ApplyServers(IReadOnlyList<KoboldKareServerEntry> servers) {
        ClearRoomList();
        int visibleCount = 0;
        for (int i = 0; i < servers.Count; i++) {
            KoboldKareServerEntry server = servers[i];
            if (string.IsNullOrWhiteSpace(server.Endpoint) || GetBlackListed(server.DisplayName, out _)) {
                continue;
            }

            GameObject room = Instantiate(roomPrefab, transform);
            roomPrefabs.Add(room);
            SetupRoom(room, server);
            visibleCount++;
        }

        if (hideOnRoomsFound != null) {
            hideOnRoomsFound.SetActive(visibleCount == 0);
        }
    }

    private void SetupRoom(GameObject room, KoboldKareServerEntry server) {
        Transform nameTransform = room.transform.Find("Name");
        if (nameTransform != null && nameTransform.TryGetComponent(out TextMeshProUGUI nameLabel)) {
            nameLabel.text = server.DisplayName;
        }

        Transform infoTransform = room.transform.Find("Info");
        if (infoTransform != null && infoTransform.TryGetComponent(out TextMeshProUGUI infoLabel)) {
            infoLabel.text = string.IsNullOrWhiteSpace(server.Description)
                ? server.Endpoint
                : server.Description;
        }

        // Basis directory entries currently describe reachable endpoints rather than Photon room
        // open/closed state. Keep the legacy lock/icon visible only if the directory provider marks
        // the server through its own description/UI data in a future integration pass.
        Transform imageTransform = room.transform.Find("Image");
        if (imageTransform != null) {
            imageTransform.gameObject.SetActive(false);
        }

        Button button = room.GetComponent<Button>();
        if (button == null) {
            return;
        }
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(() => {
            NetworkManager manager = networkManager != null ? networkManager : NetworkManager.instance;
            if (manager != null) {
                GameManager.instance.StartCoroutine(manager.JoinBasisServer(server.Endpoint));
            }
        });
    }

    public static bool GetBlackListed(string name, out string filtered) {
        blacklist ??= WordFilter.NaughtyList.GetNaughtyList("Y3ViCmtpZAprdWIKeW91bmcKYmFieQpjdWJieQpib3ljdWIKYm9pY3ViCmdpcmxjdWIKZ3VybGN1YgpsaWxib2kKbGlsZ2lybApsaWxndXJsCmxpbG9uZQphZ2VwbGF5CnBlZG8KbmlnZ2VyCnRyYW5ueQpkaWtlCnJldGFyZApqYWlsYmFpdApuaWdnYQpuZWdybwpwYWVkbwpzaGVtYWxlCnNwaWMKc3BpY2sKem9vcGhpbGlhCmxvbGkKbGl0dGxlY3ViCmxpdHRsZWJveQpsaXR0bGVib2kKbGl0dGxlZ2lybApsaXR0bGVndXJsCmxpdHRsZW9uZQpjdWJib2kKY3ViYm95CmN1YmdpcmwKY3ViZ3VybA==");
        return WordFilter.WordFilter.GetBlackListed(name, blacklist, out filtered, true);
    }

    private void ClearRoomList() {
        for (int i = 0; i < roomPrefabs.Count; i++) {
            if (roomPrefabs[i] != null) {
                Destroy(roomPrefabs[i]);
            }
        }
        roomPrefabs.Clear();
    }
}
