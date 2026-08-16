using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KoboldKare.Basis.Networking
{
    /// <summary>
    /// Session-wide KoboldKare network coordinator. It multiplexes prefab lifecycle over one
    /// deterministic BasisNetworkBehaviour and keeps a bounded spawn ledger for late joiners.
    /// Gameplay state/RPC adapters can be layered on top without depending on Photon.
    /// </summary>
    public sealed class KoboldKareNetworkWorld : BasisNetworkBehaviour
    {
        private readonly struct KoboldKareStaticPersistentStateSnapshot
        {
            public readonly string InstanceId;
            public readonly string SceneName;
            public readonly byte[] Payload;

            public KoboldKareStaticPersistentStateSnapshot(
                string instanceId,
                string sceneName,
                byte[] payload)
            {
                InstanceId = instanceId;
                SceneName = sceneName;
                Payload = payload ?? Array.Empty<byte>();
            }
        }

        public static KoboldKareNetworkWorld Instance { get; private set; }

        [SerializeField] private KoboldKarePrefabCatalog prefabCatalog;
        [SerializeField] private Transform dynamicRoot;
        [SerializeField, Min(1)] private int maxLiveEntitiesPerPlayer = 512;
        [SerializeField, Min(1)] private int maxSpawnLedgerEntries = 4096;
        [SerializeField, Min(1)] private int maxStaticPersistentStateEntries = 512;
        [SerializeField, Min(1)] private int snapshotEntriesPerFrame = 32;
        [SerializeField, Min(1)] private int authorityRecoveryAttempts = 6;
        [SerializeField, Min(0.05f)] private float authorityRecoveryInitialDelaySeconds = 0.1f;

        private readonly Dictionary<string, KoboldKareSpawnDescriptor> spawnLedger =
            new Dictionary<string, KoboldKareSpawnDescriptor>(StringComparer.Ordinal);
        private readonly Dictionary<string, KoboldKareNetworkEntity> liveEntities =
            new Dictionary<string, KoboldKareNetworkEntity>(StringComparer.Ordinal);
        private readonly Dictionary<int, KoboldKareNetworkEntity> liveEntitiesByLegacyViewId =
            new Dictionary<int, KoboldKareNetworkEntity>();
        private readonly Dictionary<int, string> legacyViewIdReservations =
            new Dictionary<int, string>();
        private readonly Dictionary<string, byte[]> staticPersistentApplicationState =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> staticPersistentApplicationStateScene =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<ushort, int> liveEntityCountByOwner =
            new Dictionary<ushort, int>();
        private readonly Dictionary<ushort, Coroutine> snapshotCoroutines =
            new Dictionary<ushort, Coroutine>();
        private readonly HashSet<ushort> departedPlayers = new HashSet<ushort>();
        private readonly HashSet<string> pendingLocalOwnerResolution =
            new HashSet<string>(StringComparer.Ordinal);

        private bool authorityClaimInFlight;
        private bool isDuplicateInstance;
        private Coroutine authorityRecoveryCoroutine;

        public KoboldKarePrefabCatalog PrefabCatalog
        {
            get => prefabCatalog;
            set => prefabCatalog = value;
        }

        public bool IsWorldAuthority => !isDuplicateInstance && IsLocalOwner();
        public IReadOnlyDictionary<string, KoboldKareSpawnDescriptor> SpawnLedger => spawnLedger;

        public bool TryGetWorldAuthorityPlayerId(out ushort playerId) => TryGetLiveAuthority(out playerId);

        public event Action<string, byte[]> PersistentApplicationStateSnapshotReceived;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                isDuplicateInstance = true;
                BasisDebug.LogError($"Multiple {nameof(KoboldKareNetworkWorld)} instances are active. Keeping '{Instance.name}' and disabling '{name}'.");
                enabled = false;
                return;
            }
            Instance = this;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;

            transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            Vector3 scale = transform.localScale;
            AssignContentIdentifier(new BasisContentInformation
            {
                LoadedNetID = "koboldkare/world",
                UUIDOfCreator = string.Empty,
                IsAdminLocked = false,
                LoadStrategy = 0,
                PositionX = position.x,
                PositionY = position.y,
                PositionZ = position.z,
                QuaternionX = rotation.x,
                QuaternionY = rotation.y,
                QuaternionZ = rotation.z,
                QuaternionW = rotation.w,
                ScaleX = scale.x,
                ScaleY = scale.y,
                ScaleZ = scale.z,
                ModifyScale = false,
                Mode = 0,
                Persist = false,
                Static = true,
                StaticAdminLocked = false,
            });
        }

        public override void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }

        public override void OnNetworkReady()
        {
            if (isDuplicateInstance)
            {
                return;
            }

            base.OnNetworkReady();
            BeginNetworkSynchronization();
        }

        public override void OnOwnershipTransfer(BasisNetworkPlayer newOwner)
        {
            if (isDuplicateInstance)
            {
                return;
            }

            base.OnOwnershipTransfer(newOwner);
            if (newOwner != null)
            {
                departedPlayers.Remove(newOwner.playerId);
            }

            if (HasLiveAuthority())
            {
                StopAuthorityRecovery();
            }

            if (!IsWorldAuthority && TryGetLiveAuthority(out ushort authorityPlayerId))
            {
                RequestSnapshot(authorityPlayerId);
            }
        }

        public override void OnServerOwnershipDestroyed()
        {
            if (isDuplicateInstance)
            {
                return;
            }

            base.OnServerOwnershipDestroyed();
            StartAuthorityRecovery();
        }

        public override void OnPlayerJoined(BasisNetworkPlayer player)
        {
            if (isDuplicateInstance)
            {
                return;
            }

            base.OnPlayerJoined(player);
            if (player == null)
            {
                return;
            }

            departedPlayers.Remove(player.playerId);
            if (IsWorldAuthority)
            {
                SendSnapshot(player.playerId);
            }
            else if (!HasLiveAuthority())
            {
                StartAuthorityRecovery();
            }
        }

        public override void OnPlayerLeft(BasisNetworkPlayer player)
        {
            if (isDuplicateInstance)
            {
                return;
            }

            base.OnPlayerLeft(player);
            if (player == null)
            {
                return;
            }

            departedPlayers.Add(player.playerId);
            CancelSnapshot(player.playerId);
            if (!TryGetLiveAuthority(out _) || CurrentOwnerId == player.playerId)
            {
                StartAuthorityRecovery();
            }
        }

        public GameObject Spawn(
            string prefabId,
            Vector3 position,
            Quaternion rotation,
            byte[] initialPayload = null)
        {
            return Spawn(prefabId, position, rotation, Vector3.one, initialPayload);
        }

        public GameObject Spawn(
            string prefabId,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            byte[] initialPayload = null)
        {
            return SpawnInternal(
                prefabId,
                GenerateUniqueInstanceId(),
                position,
                rotation,
                scale,
                initialPayload);
        }

        /// <summary>
        /// Spawns a dynamic entity while preserving an integer Photon-era ViewID. This is used by
        /// the save migration path so references stored in old KoboldKare saves remain resolvable.
        /// </summary>
        public GameObject SpawnWithLegacyViewId(
            string prefabId,
            int legacyViewId,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            byte[] initialPayload = null)
        {
            if (legacyViewId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(legacyViewId), "Legacy view id must be positive.");
            }

            string instanceId = GenerateInstanceIdForLegacyViewId(legacyViewId);
            return SpawnInternal(prefabId, instanceId, position, rotation, scale, initialPayload);
        }

        private GameObject SpawnInternal(
            string prefabId,
            string instanceId,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            byte[] initialPayload)
        {
            if (string.IsNullOrWhiteSpace(prefabId))
            {
                throw new ArgumentException("A prefab id is required.", nameof(prefabId));
            }
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                throw new ArgumentException("An instance id is required.", nameof(instanceId));
            }

            bool hasLocalPlayerId = BasisNetworkConnection.TryGetLocalPlayerID(out ushort localPlayerId);
            if (BasisNetworkConnection.LocalPlayerIsConnected && !hasLocalPlayerId)
            {
                BasisDebug.LogWarning("Cannot spawn a networked KoboldKare entity until the local Basis player id is available.");
                return null;
            }

            KoboldKareSpawnDescriptor descriptor = new KoboldKareSpawnDescriptor
            {
                InstanceId = instanceId,
                PrefabId = prefabId,
                OwnerPlayerId = hasLocalPlayerId ? localPlayerId : default,
                Position = position,
                Rotation = rotation,
                Scale = scale,
                InitialPayload = initialPayload ?? Array.Empty<byte>(),
                PersistentApplicationPayload = Array.Empty<byte>(),
            };

            if (!TryRegisterAndSpawn(descriptor, out KoboldKareNetworkEntity entity))
            {
                return null;
            }

            if (!hasLocalPlayerId)
            {
                pendingLocalOwnerResolution.Add(descriptor.InstanceId);
            }

            if (HasNetworkID && BasisNetworkConnection.LocalPlayerIsConnected)
            {
                SendCustomNetworkEvent(
                    KoboldKareNetworkProtocol.EncodeSpawn(KoboldKareNetworkMessageKind.SpawnCommit, descriptor),
                    DeliveryMethod.ReliableOrdered);
            }

            return entity.gameObject;
        }

        public bool Despawn(GameObject instance)
        {
            if (instance == null || !instance.TryGetComponent(out KoboldKareNetworkEntity entity))
            {
                return false;
            }

            return Despawn(entity);
        }

        public bool Despawn(KoboldKareNetworkEntity entity)
        {
            if (entity == null || string.IsNullOrEmpty(entity.InstanceId))
            {
                return false;
            }

            if (HasNetworkID && !entity.IsMine && !IsWorldAuthority)
            {
                BasisDebug.LogWarning($"Ignoring despawn for '{entity.InstanceId}' because the local player is neither its owner nor world authority.");
                return false;
            }

            string instanceId = entity.InstanceId;
            ApplyDespawn(instanceId);

            if (HasNetworkID && BasisNetworkConnection.LocalPlayerIsConnected)
            {
                SendCustomNetworkEvent(
                    KoboldKareNetworkProtocol.EncodeDespawn(instanceId),
                    DeliveryMethod.ReliableOrdered);
            }

            return true;
        }

        public bool TryGetEntity(string instanceId, out KoboldKareNetworkEntity entity) =>
            liveEntities.TryGetValue(instanceId ?? string.Empty, out entity) && entity != null;

        public bool TryGetEntity(int legacyViewId, out KoboldKareNetworkEntity entity)
        {
            entity = null;
            return legacyViewId > 0 &&
                   liveEntitiesByLegacyViewId.TryGetValue(legacyViewId, out entity) &&
                   entity != null;
        }

        public bool RecordPersistentApplicationMessage(string instanceId, ushort routeId, byte[] payload)
        {
            if (string.IsNullOrEmpty(instanceId))
            {
                return false;
            }

            if (spawnLedger.TryGetValue(instanceId, out KoboldKareSpawnDescriptor descriptor))
            {
                if (!KoboldKarePersistentMessageHistory.TryAppend(
                        descriptor.PersistentApplicationPayload,
                        routeId,
                        payload,
                        out byte[] updatedHistory))
                {
                    BasisDebug.LogWarning(
                        $"Rejected persistent application state for KoboldKare entity '{instanceId}' because its bounded history is full or malformed.");
                    return false;
                }

                descriptor.PersistentApplicationPayload = updatedHistory;
                spawnLedger[instanceId] = descriptor;
                return true;
            }

            staticPersistentApplicationState.TryGetValue(instanceId, out byte[] existingStaticHistory);
            if (existingStaticHistory == null && staticPersistentApplicationState.Count >= Mathf.Max(1, maxStaticPersistentStateEntries))
            {
                BasisDebug.LogWarning(
                    $"Rejected persistent state for static KoboldKare entity '{instanceId}' because the static-state ledger is full.");
                return false;
            }

            if (!KoboldKarePersistentMessageHistory.TryAppend(
                    existingStaticHistory,
                    routeId,
                    payload,
                    out byte[] updatedStaticHistory))
            {
                BasisDebug.LogWarning(
                    $"Rejected persistent application state for static KoboldKare entity '{instanceId}' because its bounded history is full or malformed.");
                return false;
            }

            staticPersistentApplicationState[instanceId] = updatedStaticHistory;
            staticPersistentApplicationStateScene[instanceId] = SceneManager.GetActiveScene().name;
            return true;
        }

        public bool TryGetPersistentApplicationState(string instanceId, out byte[] history)
        {
            history = Array.Empty<byte>();
            if (string.IsNullOrEmpty(instanceId))
            {
                return false;
            }

            if (spawnLedger.TryGetValue(instanceId, out KoboldKareSpawnDescriptor descriptor) &&
                descriptor.PersistentApplicationPayload is { Length: > 0 })
            {
                history = descriptor.PersistentApplicationPayload;
                return true;
            }

            return staticPersistentApplicationState.TryGetValue(instanceId, out history) && history != null && history.Length > 0;
        }

        internal void NotifyEntityDestroyed(KoboldKareNetworkEntity entity)
        {
            if (isDuplicateInstance || entity == null || string.IsNullOrEmpty(entity.InstanceId))
            {
                return;
            }

            string instanceId = entity.InstanceId;
            if (!liveEntities.TryGetValue(instanceId, out KoboldKareNetworkEntity tracked) || tracked != entity)
            {
                return;
            }

            liveEntities.Remove(instanceId);
            RemoveLegacyViewId(instanceId, entity.LegacyViewId);
            pendingLocalOwnerResolution.Remove(instanceId);
            if (spawnLedger.TryGetValue(instanceId, out KoboldKareSpawnDescriptor descriptor))
            {
                spawnLedger.Remove(instanceId);
                DecrementOwnerCount(descriptor.OwnerPlayerId);
            }

            if (HasNetworkID && BasisNetworkConnection.LocalPlayerIsConnected && (entity.IsMine || IsWorldAuthority))
            {
                SendCustomNetworkEvent(
                    KoboldKareNetworkProtocol.EncodeDespawn(instanceId),
                    DeliveryMethod.ReliableOrdered);
            }
        }

        public override void OnNetworkMessage(ushort playerID, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            if (isDuplicateInstance)
            {
                return;
            }

            if (!KoboldKareNetworkProtocol.TryDecode(buffer, out KoboldKareDecodedNetworkMessage message))
            {
                BasisDebug.LogWarning($"Rejected malformed KoboldKare network message from player {playerID}.");
                return;
            }

            switch (message.Kind)
            {
                case KoboldKareNetworkMessageKind.SpawnCommit:
                {
                    KoboldKareSpawnDescriptor descriptor = message.Spawn;
                    // Normal live spawns are actor-owned. Do not trust a sender-provided owner id.
                    descriptor.OwnerPlayerId = playerID;
                    TryRegisterAndSpawn(descriptor, out _);
                    break;
                }
                case KoboldKareNetworkMessageKind.DespawnCommit:
                    if (CanPlayerDespawn(playerID, message.InstanceId))
                    {
                        ApplyDespawn(message.InstanceId);
                    }
                    else
                    {
                        BasisDebug.LogWarning($"Rejected unauthorized KoboldKare despawn of '{message.InstanceId}' from player {playerID}.");
                    }
                    break;
                case KoboldKareNetworkMessageKind.SnapshotRequest:
                    if (IsWorldAuthority)
                    {
                        SendSnapshot(playerID);
                    }
                    break;
                case KoboldKareNetworkMessageKind.SnapshotSpawn:
                    if (TryGetLiveAuthority(out ushort authorityPlayerId) && playerID == authorityPlayerId)
                    {
                        TryRegisterAndSpawn(message.Spawn, out _);
                    }
                    else
                    {
                        BasisDebug.LogWarning($"Rejected KoboldKare snapshot entry from non-authority player {playerID}.");
                    }
                    break;
                case KoboldKareNetworkMessageKind.SnapshotPersistentApplicationState:
                    if (TryGetLiveAuthority(out ushort persistentAuthorityPlayerId) && playerID == persistentAuthorityPlayerId)
                    {
                        bool alreadyTracked = staticPersistentApplicationState.ContainsKey(message.InstanceId);
                        if (!alreadyTracked &&
                            staticPersistentApplicationState.Count >= Mathf.Max(1, maxStaticPersistentStateEntries))
                        {
                            BasisDebug.LogWarning(
                                $"Rejected persistent state for static KoboldKare entity '{message.InstanceId}' because the static-state ledger is full.");
                            break;
                        }

                        staticPersistentApplicationState[message.InstanceId] = message.PersistentApplicationPayload;
                        staticPersistentApplicationStateScene[message.InstanceId] =
                            message.PersistentApplicationSceneName;
                        PersistentApplicationStateSnapshotReceived?.Invoke(
                            message.InstanceId,
                            message.PersistentApplicationPayload);
                    }
                    else
                    {
                        BasisDebug.LogWarning($"Rejected KoboldKare persistent-state snapshot from non-authority player {playerID}.");
                    }
                    break;
                case KoboldKareNetworkMessageKind.SnapshotComplete:
                    break;
            }
        }

        private async void BeginNetworkSynchronization()
        {
            try
            {
                await SynchronizeAfterNetworkReadyAsync();
            }
            catch (Exception exception)
            {
                if (this != null)
                {
                    BasisDebug.LogError($"KoboldKare network synchronization failed: {exception}");
                }
            }
        }

        private async Task SynchronizeAfterNetworkReadyAsync()
        {
            if (BasisNetworkConnection.TryGetLocalPlayerID(out ushort localPlayerId))
            {
                ResolvePendingLocalOwners(localPlayerId);
            }

            if (!HasLiveAuthority())
            {
                await TryClaimAuthorityAsync();
            }

            if (this == null || isDuplicateInstance || !isActiveAndEnabled)
            {
                return;
            }

            if (!IsWorldAuthority && TryGetLiveAuthority(out ushort authorityPlayerId))
            {
                RequestSnapshot(authorityPlayerId);
            }

            BroadcastExistingLocalState();

            if (!HasLiveAuthority())
            {
                StartAuthorityRecovery();
            }
        }

        private void ResolvePendingLocalOwners(ushort localPlayerId)
        {
            if (pendingLocalOwnerResolution.Count == 0)
            {
                return;
            }

            string[] pendingIds = new string[pendingLocalOwnerResolution.Count];
            pendingLocalOwnerResolution.CopyTo(pendingIds);
            for (int i = 0; i < pendingIds.Length; i++)
            {
                string instanceId = pendingIds[i];
                if (!spawnLedger.TryGetValue(instanceId, out KoboldKareSpawnDescriptor descriptor))
                {
                    pendingLocalOwnerResolution.Remove(instanceId);
                    continue;
                }

                ushort previousOwner = descriptor.OwnerPlayerId;
                descriptor.OwnerPlayerId = localPlayerId;
                spawnLedger[instanceId] = descriptor;
                if (previousOwner != localPlayerId)
                {
                    DecrementOwnerCount(previousOwner);
                    IncrementOwnerCount(localPlayerId);
                }

                if (liveEntities.TryGetValue(instanceId, out KoboldKareNetworkEntity entity) && entity != null)
                {
                    entity.ResolveInitialOwner(localPlayerId);
                }
                pendingLocalOwnerResolution.Remove(instanceId);
            }
        }

        private void StartAuthorityRecovery()
        {
            if (isDuplicateInstance || authorityRecoveryCoroutine != null || !isActiveAndEnabled)
            {
                return;
            }
            authorityRecoveryCoroutine = StartCoroutine(AuthorityRecoveryRoutine());
        }

        private void StopAuthorityRecovery()
        {
            if (authorityRecoveryCoroutine == null)
            {
                return;
            }
            StopCoroutine(authorityRecoveryCoroutine);
            authorityRecoveryCoroutine = null;
        }

        private IEnumerator AuthorityRecoveryRoutine()
        {
            int attempts = Mathf.Max(1, authorityRecoveryAttempts);
            float initialDelay = Mathf.Max(0.05f, authorityRecoveryInitialDelaySeconds);

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (HasLiveAuthority())
                {
                    authorityRecoveryCoroutine = null;
                    yield break;
                }

                TryClaimAuthority();

                float delay = Mathf.Min(initialDelay * Mathf.Pow(2f, attempt), 2f);
                float deadline = Time.realtimeSinceStartup + delay;
                while (Time.realtimeSinceStartup < deadline)
                {
                    if (HasLiveAuthority())
                    {
                        authorityRecoveryCoroutine = null;
                        yield break;
                    }
                    yield return null;
                }
            }

            if (!HasLiveAuthority())
            {
                BasisDebug.LogWarning($"KoboldKare world authority was not recovered after {attempts} attempts.");
            }
            authorityRecoveryCoroutine = null;
        }

        private async void TryClaimAuthority()
        {
            try
            {
                await TryClaimAuthorityAsync();
            }
            catch (Exception exception)
            {
                if (this != null)
                {
                    BasisDebug.LogError($"KoboldKare world-authority claim failed: {exception}");
                }
            }
        }

        private async Task TryClaimAuthorityAsync()
        {
            if (authorityClaimInFlight || !HasNetworkID || IsWorldAuthority || HasLiveAuthority() || !IsPreferredAuthority())
            {
                return;
            }

            authorityClaimInFlight = true;
            try
            {
                BasisOwnershipResult result = await TakeOwnershipAsync();
                if (!result.Success && this != null)
                {
                    BasisDebug.LogWarning("KoboldKare world-authority ownership claim failed.");
                }
            }
            finally
            {
                authorityClaimInFlight = false;
            }
        }

        private bool IsPreferredAuthority()
        {
            if (!BasisNetworkConnection.LocalPlayerIsConnected ||
                !BasisNetworkConnection.TryGetLocalPlayerID(out ushort localPlayerId))
            {
                return false;
            }

            ushort lowest = localPlayerId;
            foreach (ushort playerId in BasisNetworkPlayers.Players.Keys)
            {
                if (departedPlayers.Contains(playerId))
                {
                    continue;
                }
                if (playerId < lowest)
                {
                    lowest = playerId;
                }
            }
            return lowest == localPlayerId;
        }

        private bool HasLiveAuthority() => TryGetLiveAuthority(out _);

        private bool TryGetLiveAuthority(out ushort authorityPlayerId)
        {
            authorityPlayerId = default;
            if (!HasNetworkID ||
                !BasisNetworkPlayers.OwnershipPairing.TryGetValue(clientIdentifier, out ushort ownerId) ||
                departedPlayers.Contains(ownerId))
            {
                return false;
            }

            if (BasisNetworkConnection.TryGetLocalPlayerID(out ushort localPlayerId) && ownerId == localPlayerId)
            {
                if (!BasisNetworkConnection.LocalPlayerIsConnected)
                {
                    return false;
                }
                authorityPlayerId = ownerId;
                return true;
            }

            if (!BasisNetworkPlayers.Players.ContainsKey(ownerId))
            {
                return false;
            }

            authorityPlayerId = ownerId;
            return true;
        }

        private void RequestSnapshot(ushort authorityPlayerId)
        {
            if (!HasNetworkID)
            {
                return;
            }

            SendCustomNetworkEvent(
                KoboldKareNetworkProtocol.EncodeControl(KoboldKareNetworkMessageKind.SnapshotRequest),
                DeliveryMethod.ReliableOrdered,
                new[] { authorityPlayerId });
        }

        private void SendSnapshot(ushort recipientPlayerId)
        {
            if (!HasNetworkID || isDuplicateInstance || !isActiveAndEnabled)
            {
                return;
            }

            CancelSnapshot(recipientPlayerId);
            KoboldKareSpawnDescriptor[] entries = new KoboldKareSpawnDescriptor[spawnLedger.Count];
            spawnLedger.Values.CopyTo(entries, 0);
            KoboldKareStaticPersistentStateSnapshot[] staticStateEntries =
                new KoboldKareStaticPersistentStateSnapshot[staticPersistentApplicationState.Count];
            int stateIndex = 0;
            foreach (KeyValuePair<string, byte[]> entry in staticPersistentApplicationState)
            {
                staticPersistentApplicationStateScene.TryGetValue(entry.Key, out string sceneName);
                staticStateEntries[stateIndex++] = new KoboldKareStaticPersistentStateSnapshot(
                    entry.Key,
                    sceneName,
                    entry.Value);
            }
            snapshotCoroutines[recipientPlayerId] = StartCoroutine(
                SendSnapshotRoutine(recipientPlayerId, entries, staticStateEntries));
        }

        private IEnumerator SendSnapshotRoutine(
            ushort recipientPlayerId,
            KoboldKareSpawnDescriptor[] entries,
            KoboldKareStaticPersistentStateSnapshot[] staticStateEntries)
        {
            // Ensure StartCoroutine returns and the handle is registered before this routine can complete.
            yield return null;

            ushort[] recipient = { recipientPlayerId };
            int perFrame = Mathf.Max(1, snapshotEntriesPerFrame);
            for (int i = 0; i < entries.Length; i++)
            {
                if (departedPlayers.Contains(recipientPlayerId) || !HasNetworkID)
                {
                    snapshotCoroutines.Remove(recipientPlayerId);
                    yield break;
                }

                SendCustomNetworkEvent(
                    KoboldKareNetworkProtocol.EncodeSpawn(KoboldKareNetworkMessageKind.SnapshotSpawn, entries[i]),
                    DeliveryMethod.ReliableOrdered,
                    recipient);

                if ((i + 1) % perFrame == 0)
                {
                    yield return null;
                }
            }

            for (int i = 0; i < staticStateEntries.Length; i++)
            {
                if (departedPlayers.Contains(recipientPlayerId) || !HasNetworkID)
                {
                    snapshotCoroutines.Remove(recipientPlayerId);
                    yield break;
                }

                KoboldKareStaticPersistentStateSnapshot state = staticStateEntries[i];
                if (string.IsNullOrWhiteSpace(state.SceneName))
                {
                    continue;
                }
                SendCustomNetworkEvent(
                    KoboldKareNetworkProtocol.EncodePersistentApplicationState(
                        state.InstanceId,
                        state.SceneName,
                        state.Payload),
                    DeliveryMethod.ReliableOrdered,
                    recipient);

                if ((i + 1) % perFrame == 0)
                {
                    yield return null;
                }
            }

            if (!departedPlayers.Contains(recipientPlayerId) && HasNetworkID)
            {
                SendCustomNetworkEvent(
                    KoboldKareNetworkProtocol.EncodeControl(KoboldKareNetworkMessageKind.SnapshotComplete),
                    DeliveryMethod.ReliableOrdered,
                    recipient);
            }
            snapshotCoroutines.Remove(recipientPlayerId);
        }

        private void CancelSnapshot(ushort recipientPlayerId)
        {
            if (!snapshotCoroutines.TryGetValue(recipientPlayerId, out Coroutine coroutine) || coroutine == null)
            {
                return;
            }
            StopCoroutine(coroutine);
            snapshotCoroutines.Remove(recipientPlayerId);
        }

        private void BroadcastExistingLocalState()
        {
            if (!HasNetworkID || !BasisNetworkConnection.LocalPlayerIsConnected ||
                !BasisNetworkConnection.TryGetLocalPlayerID(out ushort localPlayerId))
            {
                return;
            }

            foreach (KoboldKareSpawnDescriptor descriptor in spawnLedger.Values)
            {
                if (descriptor.OwnerPlayerId != localPlayerId)
                {
                    continue;
                }

                SendCustomNetworkEvent(
                    KoboldKareNetworkProtocol.EncodeSpawn(KoboldKareNetworkMessageKind.SpawnCommit, descriptor),
                    DeliveryMethod.ReliableOrdered);
            }
        }

        private bool TryRegisterAndSpawn(
            in KoboldKareSpawnDescriptor descriptor,
            out KoboldKareNetworkEntity entity)
        {
            entity = null;
            if (string.IsNullOrWhiteSpace(descriptor.InstanceId) ||
                string.IsNullOrWhiteSpace(descriptor.PrefabId) ||
                !KoboldKareLegacyViewId.TryFromInstanceId(descriptor.InstanceId, out int legacyViewId) ||
                !IsFinite(descriptor.Position) ||
                !IsFinite(descriptor.Rotation) ||
                !IsFinite(descriptor.Scale))
            {
                BasisDebug.LogWarning("Rejected invalid KoboldKare spawn descriptor.");
                return false;
            }

            if (legacyViewIdReservations.TryGetValue(legacyViewId, out string reservedInstanceId) &&
                !string.Equals(reservedInstanceId, descriptor.InstanceId, StringComparison.Ordinal))
            {
                BasisDebug.LogWarning($"Rejected KoboldKare spawn '{descriptor.InstanceId}' because legacy view id {legacyViewId} is already reserved by '{reservedInstanceId}'.");
                return false;
            }

            bool alreadyRegistered = spawnLedger.TryGetValue(descriptor.InstanceId, out KoboldKareSpawnDescriptor registeredDescriptor);
            if (alreadyRegistered)
            {
                if (!string.Equals(registeredDescriptor.PrefabId, descriptor.PrefabId, StringComparison.Ordinal) ||
                    registeredDescriptor.OwnerPlayerId != descriptor.OwnerPlayerId)
                {
                    BasisDebug.LogWarning($"Rejected conflicting KoboldKare spawn for existing id '{descriptor.InstanceId}'.");
                    return false;
                }

                if (liveEntities.TryGetValue(descriptor.InstanceId, out entity) && entity != null)
                {
                    return true;
                }
            }
            else if (!CanRegisterNewSpawn(descriptor.OwnerPlayerId))
            {
                return false;
            }

            GameObject prefab;
            bool hasPrefab = KoboldKareRuntimePrefabRegistry.TryGet(descriptor.PrefabId, out prefab) ||
                             (prefabCatalog != null && prefabCatalog.TryGetPrefab(descriptor.PrefabId, out prefab));
            if (!hasPrefab || prefab == null)
            {
                BasisDebug.LogError($"No KoboldKare network prefab is registered for id '{descriptor.PrefabId}'.");
                return false;
            }

            // Leave dynamic entities in the active gameplay scene unless a scene-local root is
            // explicitly supplied. The world coordinator itself may be DontDestroyOnLoad.
            Transform parent = dynamicRoot;
            GameObject instance = Instantiate(prefab, descriptor.Position, descriptor.Rotation, parent);
            instance.name = $"{prefab.name} [{descriptor.InstanceId}]";
            instance.transform.localScale = descriptor.Scale;

            entity = instance.GetComponent<KoboldKareNetworkEntity>();
            if (entity == null)
            {
                entity = instance.AddComponent<KoboldKareNetworkEntity>();
            }
            entity.Configure(descriptor);

            if (!alreadyRegistered)
            {
                spawnLedger.Add(descriptor.InstanceId, descriptor);
                legacyViewIdReservations.Add(legacyViewId, descriptor.InstanceId);
                IncrementOwnerCount(descriptor.OwnerPlayerId);
            }
            liveEntities[descriptor.InstanceId] = entity;
            liveEntitiesByLegacyViewId[legacyViewId] = entity;
            return true;
        }

        private bool CanRegisterNewSpawn(ushort ownerPlayerId)
        {
            int totalLimit = Mathf.Max(1, maxSpawnLedgerEntries);
            if (spawnLedger.Count >= totalLimit)
            {
                BasisDebug.LogWarning($"Rejected KoboldKare spawn because the session ledger reached its {totalLimit}-entity limit.");
                return false;
            }

            int perPlayerLimit = Mathf.Max(1, maxLiveEntitiesPerPlayer);
            liveEntityCountByOwner.TryGetValue(ownerPlayerId, out int currentCount);
            if (currentCount >= perPlayerLimit)
            {
                BasisDebug.LogWarning($"Rejected KoboldKare spawn for player {ownerPlayerId} because they reached the {perPlayerLimit}-entity limit.");
                return false;
            }

            return true;
        }

        private bool CanPlayerDespawn(ushort playerId, string instanceId)
        {
            if (TryGetLiveAuthority(out ushort authorityPlayerId) && playerId == authorityPlayerId)
            {
                return true;
            }

            return spawnLedger.TryGetValue(instanceId ?? string.Empty, out KoboldKareSpawnDescriptor descriptor) &&
                   descriptor.OwnerPlayerId == playerId;
        }

        private void ApplyDespawn(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
            {
                return;
            }

            pendingLocalOwnerResolution.Remove(instanceId);
            if (spawnLedger.TryGetValue(instanceId, out KoboldKareSpawnDescriptor descriptor))
            {
                spawnLedger.Remove(instanceId);
                if (KoboldKareLegacyViewId.TryFromInstanceId(instanceId, out int removedLegacyViewId))
                {
                    RemoveLegacyViewId(instanceId, removedLegacyViewId);
                }
                DecrementOwnerCount(descriptor.OwnerPlayerId);
            }

            if (!liveEntities.TryGetValue(instanceId, out KoboldKareNetworkEntity entity) || entity == null)
            {
                liveEntities.Remove(instanceId);
                return;
            }

            // Remove before Destroy so the entity OnDestroy callback can distinguish an intentional despawn
            // from old KoboldKare code destroying a network entity directly.
            RemoveLegacyViewId(instanceId, entity.LegacyViewId);
            liveEntities.Remove(instanceId);
            Destroy(entity.gameObject);
        }

        private string GenerateUniqueInstanceId()
        {
            for (int attempt = 0; attempt < 32; attempt++)
            {
                string candidate = Guid.NewGuid().ToString("N");
                if (!KoboldKareLegacyViewId.TryFromInstanceId(candidate, out int legacyViewId))
                {
                    continue;
                }
                if (!spawnLedger.ContainsKey(candidate) && !legacyViewIdReservations.ContainsKey(legacyViewId))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("Unable to allocate a collision-free KoboldKare network instance id.");
        }

        private string GenerateInstanceIdForLegacyViewId(int legacyViewId)
        {
            if (legacyViewIdReservations.TryGetValue(legacyViewId, out string reservedInstanceId))
            {
                throw new InvalidOperationException(
                    $"Legacy KoboldKare view id {legacyViewId} is already reserved by '{reservedInstanceId}'.");
            }

            string prefix = ((uint)legacyViewId).ToString("x8");
            for (int attempt = 0; attempt < 32; attempt++)
            {
                string random = Guid.NewGuid().ToString("N");
                string candidate = prefix + random.Substring(8);
                if (!spawnLedger.ContainsKey(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                $"Unable to allocate a KoboldKare instance id for legacy view id {legacyViewId}.");
        }

        private void RemoveLegacyViewId(string instanceId, int legacyViewId)
        {
            if (legacyViewId <= 0)
            {
                return;
            }

            if (legacyViewIdReservations.TryGetValue(legacyViewId, out string reservedInstanceId) &&
                string.Equals(reservedInstanceId, instanceId, StringComparison.Ordinal))
            {
                legacyViewIdReservations.Remove(legacyViewId);
            }

            if (liveEntitiesByLegacyViewId.TryGetValue(legacyViewId, out KoboldKareNetworkEntity entity) &&
                (entity == null || string.Equals(entity.InstanceId, instanceId, StringComparison.Ordinal)))
            {
                liveEntitiesByLegacyViewId.Remove(legacyViewId);
            }
        }

        private void OnActiveSceneChanged(Scene previousScene, Scene nextScene)
        {
            if (previousScene == nextScene)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(nextScene.name))
            {
                return;
            }

            List<string> staleKeys = null;
            foreach (KeyValuePair<string, string> entry in staticPersistentApplicationStateScene)
            {
                if (string.Equals(entry.Value, nextScene.name, StringComparison.Ordinal))
                {
                    continue;
                }
                staleKeys ??= new List<string>();
                staleKeys.Add(entry.Key);
            }

            if (staleKeys == null)
            {
                return;
            }
            for (int i = 0; i < staleKeys.Count; i++)
            {
                staticPersistentApplicationState.Remove(staleKeys[i]);
                staticPersistentApplicationStateScene.Remove(staleKeys[i]);
            }
        }

        private void IncrementOwnerCount(ushort ownerPlayerId)
        {
            liveEntityCountByOwner.TryGetValue(ownerPlayerId, out int count);
            liveEntityCountByOwner[ownerPlayerId] = count + 1;
        }

        private void DecrementOwnerCount(ushort ownerPlayerId)
        {
            if (!liveEntityCountByOwner.TryGetValue(ownerPlayerId, out int count))
            {
                return;
            }

            if (count <= 1)
            {
                liveEntityCountByOwner.Remove(ownerPlayerId);
            }
            else
            {
                liveEntityCountByOwner[ownerPlayerId] = count - 1;
            }
        }

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static bool IsFinite(Quaternion value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
