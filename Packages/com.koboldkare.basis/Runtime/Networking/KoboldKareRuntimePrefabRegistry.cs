using System;
using System.Collections.Generic;
using UnityEngine;

namespace KoboldKare.Basis.Networking
{
    /// <summary>
    /// Process-local registry for KoboldKare prefabs discovered by the game's existing asset/mod
    /// pipeline. PreparePool owns override priority; this registry only stores the currently active
    /// prefab for each network prefab id.
    /// </summary>
    public static class KoboldKareRuntimePrefabRegistry
    {
        private static readonly Dictionary<string, GameObject> Prefabs =
            new Dictionary<string, GameObject>(StringComparer.Ordinal);

        public static int Count => Prefabs.Count;

        public static IReadOnlyDictionary<string, GameObject> Snapshot()
        {
            return new Dictionary<string, GameObject>(Prefabs, StringComparer.Ordinal);
        }

        public static void Register(string prefabId, GameObject prefab)
        {
            if (string.IsNullOrWhiteSpace(prefabId))
            {
                throw new ArgumentException("A prefab id is required.", nameof(prefabId));
            }
            if (prefab == null)
            {
                throw new ArgumentNullException(nameof(prefab));
            }

            Prefabs[prefabId] = prefab;
        }

        public static bool Unregister(string prefabId)
        {
            return !string.IsNullOrEmpty(prefabId) && Prefabs.Remove(prefabId);
        }

        public static bool TryGet(string prefabId, out GameObject prefab)
        {
            return Prefabs.TryGetValue(prefabId ?? string.Empty, out prefab) && prefab != null;
        }

        public static bool Contains(string prefabId)
        {
            return TryGet(prefabId, out _);
        }

        public static void Clear()
        {
            Prefabs.Clear();
        }
    }
}
