using Basis.Scripts.BasisSdk;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KoboldKare.Basis
{
    /// <summary>
    /// Basis' native local-player bootstrap expects every active gameplay scene to expose a
    /// BasisScene. KoboldKare already owns scene loading and spawn placement, so this adapter adds
    /// only the marker required by Basis and leaves SpawnPoint unset. The possessed Kobold bridge
    /// subsequently owns the local player's gameplay position.
    /// </summary>
    public static class KoboldKareBasisSceneAdapter
    {
        private const string MarkerName = "KoboldKare Basis Scene";
        private static bool initialized;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
            EnsureScene(SceneManager.GetActiveScene());
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureScene(scene);
        }

        private static void OnActiveSceneChanged(Scene previous, Scene current)
        {
            EnsureScene(current);
        }

        private static void EnsureScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return;
            }

            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i].GetComponentInChildren<BasisScene>(true) != null)
                {
                    return;
                }
            }

            GameObject marker = new GameObject(MarkerName);
            SceneManager.MoveGameObjectToScene(marker, scene);
            marker.AddComponent<BasisScene>();
        }
    }
}
