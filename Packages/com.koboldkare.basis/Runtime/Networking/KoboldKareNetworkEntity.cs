using System;
using System.Threading.Tasks;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using UnityEngine;

namespace KoboldKare.Basis.Networking
{
    /// <summary>
    /// Basis-side replacement for the identity/ownership portion of a PhotonView.
    /// Configure must be called in the same frame the prefab is instantiated, before Start runs.
    /// </summary>
    public sealed class KoboldKareNetworkEntity : BasisNetworkBehaviour
    {
        [SerializeField, HideInInspector] private string instanceId;
        [SerializeField, HideInInspector] private string prefabId;
        [SerializeField, HideInInspector] private ushort initialOwnerPlayerId;

        private bool configured;
        private bool ownershipClaimInFlight;
        private byte[] initialPayload = Array.Empty<byte>();
        private byte[] persistentApplicationPayload = Array.Empty<byte>();

        public string InstanceId => instanceId;
        public string PrefabId => prefabId;
        public ushort InitialOwnerPlayerId => initialOwnerPlayerId;
        public ushort OwnerPlayerId => CurrentOwnerId;
        public int LegacyViewId => KoboldKareLegacyViewId.TryFromInstanceId(instanceId, out int viewId) ? viewId : 0;
        public ReadOnlyMemory<byte> InitialPayload => initialPayload;
        public ReadOnlyMemory<byte> PersistentApplicationPayload => persistentApplicationPayload;
        public bool IsConfigured => configured;

        /// <summary>
        /// Raw, bounded gameplay messages delivered over this entity's Basis network channel.
        /// The first ushort is the application route id; higher-level KoboldKare adapters own
        /// serialization and invocation semantics for each route.
        /// </summary>
        public event Action<ushort, ushort, byte[], DeliveryMethod> ApplicationMessageReceived;

        public bool IsMine
        {
            get
            {
                if (HasNetworkID)
                {
                    return IsLocalOwner();
                }

                return configured &&
                       BasisNetworkConnection.TryGetLocalPlayerID(out ushort localPlayerId) &&
                       localPlayerId == initialOwnerPlayerId;
            }
        }

        public void Configure(in KoboldKareSpawnDescriptor descriptor)
        {
            if (configured)
            {
                if (!string.Equals(instanceId, descriptor.InstanceId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Network entity is already configured as '{instanceId}'.");
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(descriptor.InstanceId))
            {
                throw new ArgumentException("A network instance id is required.", nameof(descriptor));
            }
            if (string.IsNullOrWhiteSpace(descriptor.PrefabId))
            {
                throw new ArgumentException("A network prefab id is required.", nameof(descriptor));
            }
            if (!KoboldKareLegacyViewId.TryFromInstanceId(descriptor.InstanceId, out _))
            {
                throw new ArgumentException("The network instance id cannot produce a valid legacy view id.", nameof(descriptor));
            }

            instanceId = descriptor.InstanceId;
            prefabId = descriptor.PrefabId;
            initialOwnerPlayerId = descriptor.OwnerPlayerId;
            initialPayload = descriptor.InitialPayload ?? Array.Empty<byte>();
            persistentApplicationPayload = descriptor.PersistentApplicationPayload ?? Array.Empty<byte>();
            configured = true;

            transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            Vector3 scale = transform.localScale;
            AssignContentIdentifier(new BasisContentInformation
            {
                LoadedNetID = $"koboldkare/entity/{instanceId}",
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
                ModifyScale = true,
                Mode = 0,
                Persist = false,
                Static = false,
                StaticAdminLocked = false,
            });
        }

        public Task<BasisOwnershipResult> TransferOwnershipToAsync(ushort playerId, int timeoutMs = 5000)
        {
            if (!HasNetworkID)
            {
                return Task.FromResult(BasisOwnershipResult.Failed);
            }
            return BasisNetworkOwnership.TakeOwnershipAsync(clientIdentifier, playerId, timeoutMs);
        }

        public void SendApplicationMessage(
            ushort routeId,
            byte[] payload = null,
            DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered,
            ushort[] recipients = null)
        {
            byte[] envelope = KoboldKareEntityMessageProtocol.Encode(routeId, payload);
            SendCustomNetworkEvent(envelope, deliveryMethod, recipients);
        }

        public void SendApplicationMessageDirect(
            ushort routeId,
            byte[] payload = null,
            DeliveryMethod deliveryMethod = DeliveryMethod.Unreliable,
            ushort[] recipients = null,
            bool allowServerFallback = true)
        {
            byte[] envelope = KoboldKareEntityMessageProtocol.Encode(routeId, payload);
            SendCustomNetworkEventDirect(envelope, deliveryMethod, recipients, allowServerFallback);
        }

        public override void OnNetworkReady()
        {
            base.OnNetworkReady();
            if (!configured)
            {
                BasisDebug.LogError($"{nameof(KoboldKareNetworkEntity)} on '{name}' reached network ready before it was configured.");
                return;
            }

            if (BasisNetworkConnection.TryGetLocalPlayerID(out ushort localPlayerId) &&
                localPlayerId == initialOwnerPlayerId &&
                !IsLocalOwner())
            {
                BeginInitialOwnershipClaim();
            }
        }

        public override void OnNetworkMessage(ushort playerID, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            DispatchApplicationMessage(playerID, buffer, deliveryMethod);
        }

        public override void OnDirectNetworkMessage(ushort playerID, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            DispatchApplicationMessage(playerID, buffer, deliveryMethod);
        }

        internal void ResolveInitialOwner(ushort playerId)
        {
            initialOwnerPlayerId = playerId;
            if (HasNetworkID &&
                BasisNetworkConnection.TryGetLocalPlayerID(out ushort localPlayerId) &&
                localPlayerId == playerId &&
                !IsLocalOwner())
            {
                BeginInitialOwnershipClaim();
            }
        }

        public override void OnDestroy()
        {
            KoboldKareNetworkWorld.Instance?.NotifyEntityDestroyed(this);
            ApplicationMessageReceived = null;
            base.OnDestroy();
        }

        private void DispatchApplicationMessage(ushort playerId, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            if (!KoboldKareEntityMessageProtocol.TryDecode(buffer, out KoboldKareEntityMessage message))
            {
                BasisDebug.LogWarning($"Rejected malformed KoboldKare entity message for '{instanceId}' from player {playerId}.");
                return;
            }

            Delegate[] handlers = ApplicationMessageReceived?.GetInvocationList();
            if (handlers == null)
            {
                return;
            }

            for (int i = 0; i < handlers.Length; i++)
            {
                try
                {
                    ((Action<ushort, ushort, byte[], DeliveryMethod>)handlers[i])(
                        playerId,
                        message.RouteId,
                        message.Payload,
                        deliveryMethod);
                }
                catch (Exception exception)
                {
                    BasisDebug.LogError($"KoboldKare entity route {message.RouteId} handler failed on '{instanceId}': {exception}");
                }
            }
        }

        private async void BeginInitialOwnershipClaim()
        {
            if (ownershipClaimInFlight || this == null)
            {
                return;
            }

            ownershipClaimInFlight = true;
            try
            {
                BasisOwnershipResult result = await TakeOwnershipAsync();
                if (this != null && !result.Success)
                {
                    BasisDebug.LogWarning($"Failed to claim KoboldKare entity ownership for '{instanceId}'.");
                }
            }
            catch (Exception exception)
            {
                if (this != null)
                {
                    BasisDebug.LogError($"KoboldKare entity ownership claim failed for '{instanceId}': {exception}");
                }
            }
            finally
            {
                ownershipClaimInFlight = false;
            }
        }
    }
}
