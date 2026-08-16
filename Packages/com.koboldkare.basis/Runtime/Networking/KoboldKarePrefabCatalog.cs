using System;
using System.Collections.Generic;
using UnityEngine;

namespace KoboldKare.Basis.Networking
{
    [CreateAssetMenu(fileName = "KoboldKarePrefabCatalog", menuName = "KoboldKare/Basis Network Prefab Catalog")]
    public sealed class KoboldKarePrefabCatalog : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public string PrefabId;
            public GameObject Prefab;
        }

        [SerializeField] private List<Entry> entries = new List<Entry>();
        private readonly Dictionary<string, GameObject> cache = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        private bool cacheBuilt;

        public bool TryGetPrefab(string prefabId, out GameObject prefab)
        {
            if (KoboldKareRuntimePrefabRegistry.TryGet(prefabId, out prefab))
            {
                return true;
            }

            EnsureCache();
            return cache.TryGetValue(prefabId ?? string.Empty, out prefab) && prefab != null;
        }

        public bool ContainsPrefab(string prefabId)
        {
            return TryGetPrefab(prefabId, out _);
        }

        public IReadOnlyList<Entry> Entries => entries;

        private void OnEnable()
        {
            cacheBuilt = false;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            cacheBuilt = false;
        }
#endif

        private void EnsureCache()
        {
            if (cacheBuilt)
            {
                return;
            }

            cache.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (string.IsNullOrWhiteSpace(entry.PrefabId) || entry.Prefab == null)
                {
                    continue;
                }

                if (cache.ContainsKey(entry.PrefabId))
                {
                    Debug.LogWarning($"Duplicate KoboldKare network prefab id '{entry.PrefabId}' in {name}; the first entry wins.", this);
                    continue;
                }

                cache.Add(entry.PrefabId, entry.Prefab);
            }

            cacheBuilt = true;
        }
    }
}
