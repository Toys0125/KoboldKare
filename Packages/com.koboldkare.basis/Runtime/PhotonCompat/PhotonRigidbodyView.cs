using UnityEngine;

namespace Photon.Pun
{
    /// <summary>
    /// Compatibility replacement for KoboldKare's serialized PhotonRigidbodyView components.
    /// The original serialized options are retained while Basis carries the observable payload.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class PhotonRigidbodyView : MonoBehaviourPun, IPunObservable
    {
        [HideInInspector] public bool m_SynchronizeVelocity = true;
        [HideInInspector] public bool m_SynchronizeAngularVelocity;
        [HideInInspector] public bool m_TeleportEnabled;
        [HideInInspector] public float m_TeleportIfDistanceGreaterThan = 3f;

        private Rigidbody body;
        private Vector3 networkPosition;
        private Quaternion networkRotation;
        private float distance;
        private float angle;
        private bool firstReceive = true;

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            networkPosition = body.position;
            networkRotation = body.rotation;
        }

        private void FixedUpdate()
        {
            if (photonView == null || photonView.IsMine || body == null)
            {
                return;
            }

            float stepScale = Time.fixedDeltaTime * Mathf.Max(1, PhotonNetwork.SerializationRate);
            body.position = Vector3.MoveTowards(body.position, networkPosition, distance * stepScale);
            body.rotation = Quaternion.RotateTowards(body.rotation, networkRotation, angle * stepScale);
        }

        public void CaptureSaveState(
            out Vector3 position,
            out Quaternion rotation,
            out Vector3 velocity,
            out Vector3 angularVelocity)
        {
            if (body == null)
            {
                body = GetComponent<Rigidbody>();
            }
            position = body != null ? body.position : transform.position;
            rotation = body != null ? body.rotation : transform.rotation;
            velocity = body != null ? body.linearVelocity : Vector3.zero;
            angularVelocity = body != null ? body.angularVelocity : Vector3.zero;
        }

        public void ApplySaveState(
            Vector3 position,
            Quaternion rotation,
            Vector3 velocity,
            Vector3 angularVelocity)
        {
            if (body == null)
            {
                body = GetComponent<Rigidbody>();
                if (body == null)
                {
                    return;
                }
            }

            networkPosition = position;
            networkRotation = rotation;
            body.position = position;
            body.rotation = rotation;
            if (m_SynchronizeVelocity)
            {
                body.linearVelocity = velocity;
            }
            if (m_SynchronizeAngularVelocity)
            {
                body.angularVelocity = angularVelocity;
            }
            distance = 0f;
            angle = 0f;
            firstReceive = false;
        }

        public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
        {
            if (body == null)
            {
                body = GetComponent<Rigidbody>();
                if (body == null)
                {
                    return;
                }
            }

            if (stream.IsWriting)
            {
                stream.SendNext(body.position);
                stream.SendNext(body.rotation);
                if (m_SynchronizeVelocity)
                {
                    stream.SendNext(body.linearVelocity);
                }
                if (m_SynchronizeAngularVelocity)
                {
                    stream.SendNext(body.angularVelocity);
                }
                return;
            }

            networkPosition = (Vector3)stream.ReceiveNext();
            networkRotation = (Quaternion)stream.ReceiveNext();

            if (firstReceive ||
                (m_TeleportEnabled &&
                 Vector3.Distance(body.position, networkPosition) > m_TeleportIfDistanceGreaterThan))
            {
                body.position = networkPosition;
                body.rotation = networkRotation;
                distance = 0f;
                angle = 0f;
            }
            else
            {
                distance = Vector3.Distance(body.position, networkPosition);
                angle = Quaternion.Angle(body.rotation, networkRotation);
            }

            if (m_SynchronizeVelocity)
            {
                body.linearVelocity = (Vector3)stream.ReceiveNext();
            }
            if (m_SynchronizeAngularVelocity)
            {
                body.angularVelocity = (Vector3)stream.ReceiveNext();
            }

            firstReceive = false;
        }
    }
}
