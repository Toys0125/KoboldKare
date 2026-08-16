using UnityEngine;

namespace Photon.Pun
{
    /// <summary>
    /// PUN-compatible base class retained so KoboldKare gameplay scripts can migrate to Basis
    /// without changing every cached photonView access in the first pass.
    /// </summary>
    public class MonoBehaviourPun : MonoBehaviour
    {
        private PhotonView photonViewCache;

        public PhotonView photonView
        {
            get
            {
#if UNITY_EDITOR
                if (!Application.isPlaying || photonViewCache == null)
                {
                    photonViewCache = PhotonView.Get(this);
                }
#else
                if (photonViewCache == null)
                {
                    photonViewCache = PhotonView.Get(this);
                }
#endif
                return photonViewCache;
            }
        }
    }
}
