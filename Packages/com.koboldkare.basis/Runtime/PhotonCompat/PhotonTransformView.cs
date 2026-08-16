using UnityEngine;

namespace Photon.Pun
{
    /// <summary>
    /// Compatibility replacement for KoboldKare's three serialized PhotonTransformView uses.
    /// Transport is provided by the Basis-backed PhotonView observable channel.
    /// </summary>
    public sealed class PhotonTransformView : MonoBehaviourPun, IPunObservable
    {
        public bool m_SynchronizePosition = true;
        public bool m_SynchronizeRotation = true;
        public bool m_SynchronizeScale;
        public bool m_UseLocal;

        private Vector3 networkPosition;
        private Quaternion networkRotation;
        private float distance;
        private float angle;
        private bool firstReceive = true;

        private void Awake()
        {
            networkPosition = m_UseLocal ? transform.localPosition : transform.position;
            networkRotation = m_UseLocal ? transform.localRotation : transform.rotation;
        }

        private void OnEnable()
        {
            firstReceive = true;
        }

        private void Update()
        {
            if (photonView == null || photonView.IsMine)
            {
                return;
            }

            float stepScale = Time.deltaTime * Mathf.Max(1, PhotonNetwork.SerializationRate);
            if (m_SynchronizePosition)
            {
                if (m_UseLocal)
                {
                    transform.localPosition = Vector3.MoveTowards(
                        transform.localPosition,
                        networkPosition,
                        distance * stepScale);
                }
                else
                {
                    transform.position = Vector3.MoveTowards(
                        transform.position,
                        networkPosition,
                        distance * stepScale);
                }
            }

            if (m_SynchronizeRotation)
            {
                if (m_UseLocal)
                {
                    transform.localRotation = Quaternion.RotateTowards(
                        transform.localRotation,
                        networkRotation,
                        angle * stepScale);
                }
                else
                {
                    transform.rotation = Quaternion.RotateTowards(
                        transform.rotation,
                        networkRotation,
                        angle * stepScale);
                }
            }
        }

        public void CaptureSaveState(out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            position = m_UseLocal ? transform.localPosition : transform.position;
            rotation = m_UseLocal ? transform.localRotation : transform.rotation;
            scale = transform.localScale;
        }

        public void ApplySaveState(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            if (m_SynchronizePosition)
            {
                networkPosition = position;
                if (m_UseLocal)
                {
                    transform.localPosition = position;
                }
                else
                {
                    transform.position = position;
                }
            }
            if (m_SynchronizeRotation)
            {
                networkRotation = rotation;
                if (m_UseLocal)
                {
                    transform.localRotation = rotation;
                }
                else
                {
                    transform.rotation = rotation;
                }
            }
            if (m_SynchronizeScale)
            {
                transform.localScale = scale;
            }
            distance = 0f;
            angle = 0f;
            firstReceive = false;
        }

        public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
        {
            if (stream.IsWriting)
            {
                if (m_SynchronizePosition)
                {
                    stream.SendNext(m_UseLocal ? transform.localPosition : transform.position);
                }
                if (m_SynchronizeRotation)
                {
                    stream.SendNext(m_UseLocal ? transform.localRotation : transform.rotation);
                }
                if (m_SynchronizeScale)
                {
                    stream.SendNext(transform.localScale);
                }
                return;
            }

            if (m_SynchronizePosition)
            {
                networkPosition = (Vector3)stream.ReceiveNext();
                Vector3 current = m_UseLocal ? transform.localPosition : transform.position;
                distance = Vector3.Distance(current, networkPosition);
                if (firstReceive)
                {
                    if (m_UseLocal)
                    {
                        transform.localPosition = networkPosition;
                    }
                    else
                    {
                        transform.position = networkPosition;
                    }
                    distance = 0f;
                }
            }

            if (m_SynchronizeRotation)
            {
                networkRotation = (Quaternion)stream.ReceiveNext();
                Quaternion current = m_UseLocal ? transform.localRotation : transform.rotation;
                angle = Quaternion.Angle(current, networkRotation);
                if (firstReceive)
                {
                    if (m_UseLocal)
                    {
                        transform.localRotation = networkRotation;
                    }
                    else
                    {
                        transform.rotation = networkRotation;
                    }
                    angle = 0f;
                }
            }

            if (m_SynchronizeScale)
            {
                transform.localScale = (Vector3)stream.ReceiveNext();
            }

            firstReceive = false;
        }
    }
}
