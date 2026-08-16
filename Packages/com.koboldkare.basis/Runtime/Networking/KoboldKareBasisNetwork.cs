using System;
using Basis.Scripts.Networking;
using UnityEngine;

namespace KoboldKare.Basis.Networking
{
    /// <summary>
    /// Small migration surface intended to replace the most common PhotonNetwork calls first.
    /// Keeping gameplay code pointed at this facade makes later authority/server changes local to
    /// the Basis integration package instead of spreading Basis internals through KoboldKare.
    /// </summary>
    public static class KoboldKareBasisNetwork
    {
        public static bool IsConnected => BasisNetworkConnection.LocalPlayerIsConnected;
        public static bool IsMasterClient => KoboldKareNetworkWorld.Instance != null && KoboldKareNetworkWorld.Instance.IsWorldAuthority;

        public static ushort LocalPlayerId
        {
            get
            {
                BasisNetworkConnection.TryGetLocalPlayerID(out ushort playerId);
                return playerId;
            }
        }

        public static GameObject Instantiate(
            string prefabId,
            Vector3 position,
            Quaternion rotation,
            byte[] initialPayload = null)
        {
            KoboldKareNetworkWorld world = RequireWorld();
            return world.Spawn(prefabId, position, rotation, initialPayload);
        }

        public static GameObject Instantiate(
            string prefabId,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            byte[] initialPayload = null)
        {
            KoboldKareNetworkWorld world = RequireWorld();
            return world.Spawn(prefabId, position, rotation, scale, initialPayload);
        }

        public static GameObject InstantiateWithLegacyViewId(
            string prefabId,
            int legacyViewId,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            byte[] initialPayload = null)
        {
            KoboldKareNetworkWorld world = RequireWorld();
            return world.SpawnWithLegacyViewId(
                prefabId,
                legacyViewId,
                position,
                rotation,
                scale,
                initialPayload);
        }

        public static bool Destroy(GameObject instance)
        {
            return instance != null && RequireWorld().Despawn(instance);
        }

        public static bool TryGetEntity(string instanceId, out KoboldKareNetworkEntity entity)
        {
            KoboldKareNetworkWorld world = KoboldKareNetworkWorld.Instance;
            if (world == null)
            {
                entity = null;
                return false;
            }
            return world.TryGetEntity(instanceId, out entity);
        }

        public static bool TryGetEntity(int legacyViewId, out KoboldKareNetworkEntity entity)
        {
            KoboldKareNetworkWorld world = KoboldKareNetworkWorld.Instance;
            if (world == null)
            {
                entity = null;
                return false;
            }
            return world.TryGetEntity(legacyViewId, out entity);
        }

        public static void RegisterRuntimePrefab(string prefabId, GameObject prefab)
        {
            KoboldKareRuntimePrefabRegistry.Register(prefabId, prefab);
        }

        public static bool UnregisterRuntimePrefab(string prefabId)
        {
            return KoboldKareRuntimePrefabRegistry.Unregister(prefabId);
        }

        public static bool TryGetRegisteredPrefab(string prefabId, out GameObject prefab)
        {
            if (KoboldKareRuntimePrefabRegistry.TryGet(prefabId, out prefab))
            {
                return true;
            }

            KoboldKareNetworkWorld world = KoboldKareNetworkWorld.Instance;
            if (world != null && world.PrefabCatalog != null)
            {
                return world.PrefabCatalog.TryGetPrefab(prefabId, out prefab);
            }

            prefab = null;
            return false;
        }

        public static bool IsRegisteredPrefab(string prefabId)
        {
            return TryGetRegisteredPrefab(prefabId, out _);
        }

        private static KoboldKareNetworkWorld RequireWorld()
        {
            KoboldKareNetworkWorld world = KoboldKareNetworkWorld.Instance;
            if (world == null)
            {
                throw new InvalidOperationException(
                    $"No active {nameof(KoboldKareNetworkWorld)} exists. Add one to the loaded KoboldKare map bootstrap scene.");
            }
            return world;
        }
    }
}
