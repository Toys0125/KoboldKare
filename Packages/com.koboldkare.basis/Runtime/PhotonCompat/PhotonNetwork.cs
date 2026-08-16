using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Basis.Scripts.Networking;
using KoboldKare.Basis.Networking;
using KoboldKare.Basis.PhotonCompat;
using Photon.Realtime;
using UnityEngine;

namespace Photon.Pun
{
    /// <summary>
    /// Gameplay-only PUN compatibility facade backed by Basis. Lobby/room matchmaking APIs are
    /// intentionally not reproduced; KoboldKare's NetworkManager is migrated directly to Basis.
    /// </summary>
    public static class PhotonNetwork
    {
        private static readonly HashSet<object> CallbackTargets = new HashSet<object>();
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private static bool offlineMode;

        public static int SerializationRate { get; set; } = 10;
        public static bool EnableCloseConnection { get; set; }

        public static bool OfflineMode
        {
            get => offlineMode;
            set => offlineMode = value;
        }

        public static bool IsConnected => OfflineMode || BasisNetworkConnection.LocalPlayerIsConnected;
        public static bool IsConnectedAndReady => IsConnected;
        public static bool InRoom => IsConnected;

        public static Player LocalPlayer => PhotonPlayerRegistry.LocalPlayer ?? PhotonPlayerRegistry.GetOrCreate(0);

        public static Player[] PlayerList
        {
            get
            {
                if (OfflineMode)
                {
                    return new[] { LocalPlayer };
                }
                return PhotonPlayerRegistry.GetPlayerList();
            }
        }

        public static bool IsMasterClient
        {
            get
            {
                if (OfflineMode) return true;
                return KoboldKareNetworkWorld.Instance != null && KoboldKareNetworkWorld.Instance.IsWorldAuthority;
            }
        }

        public static Player MasterClient
        {
            get
            {
                if (OfflineMode) return LocalPlayer;
                KoboldKareNetworkWorld world = KoboldKareNetworkWorld.Instance;
                return world != null && world.TryGetWorldAuthorityPlayerId(out ushort playerId)
                    ? PhotonPlayerRegistry.GetOrCreate(playerId)
                    : null;
            }
        }

        public static IEnumerable<PhotonView> PhotonViewCollection => PhotonView.RegisteredViews;

        public static PhotonView GetPhotonView(int viewId)
        {
            return PhotonView.TryGetRegistered(viewId, out PhotonView view) ? view : null;
        }

        public static GameObject Instantiate(string prefabName, Vector3 position, Quaternion rotation)
        {
            return Instantiate(prefabName, position, rotation, 0, null);
        }

        public static GameObject Instantiate(
            string prefabName,
            Vector3 position,
            Quaternion rotation,
            byte group,
            object[] data)
        {
            byte[] initialPayload = EncodeInstantiationData(data);
            GameObject instance = KoboldKareBasisNetwork.Instantiate(prefabName, position, rotation, initialPayload);
            InitializeSpawnedViews(instance);
            return instance;
        }

        public static GameObject InstantiateRoomObject(string prefabName, Vector3 position, Quaternion rotation)
        {
            return InstantiateRoomObject(prefabName, position, rotation, 0, null);
        }

        public static GameObject InstantiateRoomObject(
            string prefabName,
            Vector3 position,
            Quaternion rotation,
            byte group,
            object[] data)
        {
            if (!IsMasterClient && !OfflineMode)
            {
                Debug.LogWarning($"KoboldKare Basis compatibility rejected InstantiateRoomObject('{prefabName}') from a non-authority client.");
                return null;
            }
            return Instantiate(prefabName, position, rotation, group, data);
        }

        public static void Destroy(GameObject target)
        {
            if (target == null) return;
            if (target.TryGetComponent(out KoboldKareNetworkEntity entity))
            {
                KoboldKareNetworkWorld world = KoboldKareNetworkWorld.Instance;
                if (world != null && world.Despawn(entity))
                {
                    return;
                }
            }
            else
            {
                entity = target.GetComponentInParent<KoboldKareNetworkEntity>();
                KoboldKareNetworkWorld world = KoboldKareNetworkWorld.Instance;
                if (entity != null && world != null && world.Despawn(entity))
                {
                    return;
                }
            }

            UnityEngine.Object.Destroy(target);
        }

        public static void Destroy(PhotonView target)
        {
            if (target != null) Destroy(target.gameObject);
        }

        public static void AddCallbackTarget(object target)
        {
            if (target != null) CallbackTargets.Add(target);
        }

        public static void RemoveCallbackTarget(object target)
        {
            if (target != null) CallbackTargets.Remove(target);
        }

        internal static void ResetCompatibilitySession()
        {
            CallbackTargets.Clear();
            PhotonPlayerRegistry.ResetSessionState();
        }

        internal static void DispatchOwnershipRequest(PhotonView view, Player requestingPlayer)
        {
            object[] targets = SnapshotCallbackTargets();
            bool dispatched = false;
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] is not IPunOwnershipCallbacks callbacks) continue;
                dispatched = true;
                try
                {
                    callbacks.OnOwnershipRequest(view, requestingPlayer);
                }
                catch (Exception exception)
                {
                    Debug.LogError($"KoboldKare ownership callback failed: {exception}");
                }
            }

            // PUN's Request mode expects a callback decision. If no callback target exists, do not
            // silently grant the request; this is safer and matches the explicit KoboldKare policy.
            if (!dispatched)
            {
                Debug.LogWarning($"Ownership request for view {view?.ViewID ?? 0} had no IPunOwnershipCallbacks target.");
            }
        }

        internal static void DispatchOwnershipTransferred(PhotonView view, Player previousOwner)
        {
            object[] targets = SnapshotCallbackTargets();
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] is not IPunOwnershipCallbacks callbacks) continue;
                try
                {
                    callbacks.OnOwnershipTransfered(view, previousOwner);
                }
                catch (Exception exception)
                {
                    Debug.LogError($"KoboldKare ownership transfer callback failed: {exception}");
                }
            }
        }

        internal static byte[] EncodeInstantiationData(object[] data)
        {
            if (data == null || data.Length == 0)
            {
                return Array.Empty<byte>();
            }

            using MemoryStream stream = new MemoryStream(128);
            using BinaryWriter writer = new BinaryWriter(stream, Utf8, true);
            writer.Write((byte)1);
            PhotonCompatValueCodec.WriteValues(writer, data);
            writer.Flush();
            return stream.ToArray();
        }

        internal static bool TryDecodeInstantiationData(ReadOnlyMemory<byte> payload, out object[] data)
        {
            data = Array.Empty<object>();
            if (payload.Length == 0)
            {
                return true;
            }

            try
            {
                using MemoryStream stream = new MemoryStream(payload.ToArray(), false);
                using BinaryReader reader = new BinaryReader(stream, Utf8, true);
                if (reader.ReadByte() != 1 ||
                    !PhotonCompatValueCodec.TryReadValues(reader, out data) ||
                    stream.Position != stream.Length)
                {
                    data = Array.Empty<object>();
                    return false;
                }
                return true;
            }
            catch (Exception exception) when (
                exception is EndOfStreamException ||
                exception is IOException ||
                exception is ArgumentException)
            {
                data = Array.Empty<object>();
                return false;
            }
        }

        private static void InitializeSpawnedViews(GameObject instance)
        {
            if (instance == null) return;
            PhotonView[] views = instance.GetComponentsInChildren<PhotonView>(true);
            for (int i = 0; i < views.Length; i++)
            {
                views[i].InitializeFromEntity();
            }
        }

        private static object[] SnapshotCallbackTargets()
        {
            object[] targets = new object[CallbackTargets.Count];
            CallbackTargets.CopyTo(targets);
            return targets;
        }
    }
}
