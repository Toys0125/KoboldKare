using System;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using UnityEngine;

namespace KoboldKare.Basis.Networking
{
    /// <summary>
    /// Basis replacement for the small amount of persistent Photon room state KoboldKare uses.
    /// One Basis server session is one KoboldKare match. The elected world-authority client owns
    /// map/mod/cheat state; chat remains sender-authenticated by the Basis player id.
    /// </summary>
    public sealed class KoboldKareSessionCoordinator : BasisNetworkBehaviour
    {
        public static KoboldKareSessionCoordinator Instance { get; private set; }

        [SerializeField] private KoboldKareSessionState state;

        private bool initialized;
        private bool wasWorldAuthority;
        private bool stateRequestSent;

        public KoboldKareSessionState State => state;
        public bool HasState => !string.IsNullOrWhiteSpace(state.MapName);
        public bool IsWorldAuthority =>
            KoboldKareNetworkWorld.Instance != null && KoboldKareNetworkWorld.Instance.IsWorldAuthority;

        public event Action<KoboldKareSessionState> StateChanged;
        public event Action<ushort, string> ChatReceived;
        public event Action<ushort, string> PlayerJoined;
        public event Action<ushort, string> PlayerLeft;
        public event Action<bool> WorldAuthorityChanged;
        public event Action<string> Kicked;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                enabled = false;
                return;
            }
            Instance = this;

            transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            Vector3 scale = transform.localScale;
            AssignContentIdentifier(new BasisContentInformation
            {
                LoadedNetID = "koboldkare/session",
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
            if (Instance == this)
            {
                Instance = null;
            }
            StateChanged = null;
            ChatReceived = null;
            PlayerJoined = null;
            PlayerLeft = null;
            WorldAuthorityChanged = null;
            Kicked = null;
            base.OnDestroy();
        }

        public override void OnNetworkReady()
        {
            base.OnNetworkReady();
            initialized = true;
            wasWorldAuthority = IsWorldAuthority;
            stateRequestSent = false;
            SynchronizeStateForRole();
        }

        private void Update()
        {
            if (!initialized || !HasNetworkID || !BasisNetworkConnection.LocalPlayerIsConnected)
            {
                return;
            }

            bool isAuthority = IsWorldAuthority;
            if (isAuthority != wasWorldAuthority)
            {
                wasWorldAuthority = isAuthority;
                stateRequestSent = false;
                WorldAuthorityChanged?.Invoke(isAuthority);
                SynchronizeStateForRole();
            }
            else if (!isAuthority && !HasState && !stateRequestSent)
            {
                SynchronizeStateForRole();
            }
        }

        public bool SetSessionState(string mapName, string modListJson, bool cheatsEnabled)
        {
            if (!CanMutateAuthorityState())
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(mapName))
            {
                throw new ArgumentException("A map name is required.", nameof(mapName));
            }

            state.MapName = mapName;
            state.ModListJson = modListJson ?? string.Empty;
            state.CheatsEnabled = cheatsEnabled;
            state.Revision++;
            if (state.Revision == 0)
            {
                state.Revision = 1;
            }

            StateChanged?.Invoke(state);
            BroadcastState();
            return true;
        }

        public bool SetCheatsEnabled(bool enabled)
        {
            if (!CanMutateAuthorityState() || !HasState)
            {
                return false;
            }
            if (state.CheatsEnabled == enabled)
            {
                return true;
            }

            state.CheatsEnabled = enabled;
            state.Revision++;
            if (state.Revision == 0)
            {
                state.Revision = 1;
            }
            StateChanged?.Invoke(state);
            BroadcastState();
            return true;
        }

        public bool KickPlayer(ushort playerId, string reason = null)
        {
            if (!IsWorldAuthority || !HasNetworkID || playerId == 0)
            {
                return false;
            }
            if (BasisNetworkConnection.TryGetLocalPlayerID(out ushort localPlayerId) && playerId == localPlayerId)
            {
                return false;
            }

            byte[] payload;
            try
            {
                payload = KoboldKareSessionProtocol.EncodeKick(reason);
            }
            catch (ArgumentException exception)
            {
                BasisDebug.LogWarning($"Rejected KoboldKare kick request: {exception.Message}");
                return false;
            }

            SendCustomNetworkEvent(
                payload,
                DeliveryMethod.ReliableOrdered,
                new[] { playerId });
            return true;
        }

        public bool SendChat(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            byte[] payload;
            try
            {
                payload = KoboldKareSessionProtocol.EncodeChat(message);
            }
            catch (ArgumentException exception)
            {
                BasisDebug.LogWarning($"Rejected KoboldKare chat message: {exception.Message}");
                return false;
            }

            BasisNetworkConnection.TryGetLocalPlayerID(out ushort localPlayerId);
            ChatReceived?.Invoke(localPlayerId, message);

            if (HasNetworkID && BasisNetworkConnection.LocalPlayerIsConnected)
            {
                SendCustomNetworkEvent(payload, DeliveryMethod.ReliableOrdered);
            }
            return true;
        }

        public override void OnNetworkMessage(ushort playerID, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            if (!KoboldKareSessionProtocol.TryDecode(buffer, out KoboldKareSessionMessage message))
            {
                BasisDebug.LogWarning($"Rejected malformed KoboldKare session message from player {playerID}.");
                return;
            }

            switch (message.Kind)
            {
                case KoboldKareSessionMessageKind.StateRequest:
                    if (IsWorldAuthority && HasState)
                    {
                        SendStateTo(playerID);
                    }
                    break;
                case KoboldKareSessionMessageKind.StateSnapshot:
                    if (!TryGetAuthorityPlayerId(out ushort authorityPlayerId) || playerID != authorityPlayerId)
                    {
                        BasisDebug.LogWarning($"Rejected KoboldKare session state from non-authority player {playerID}.");
                        return;
                    }
                    ApplyRemoteState(message.State);
                    break;
                case KoboldKareSessionMessageKind.Chat:
                    ChatReceived?.Invoke(playerID, message.Text);
                    break;
                case KoboldKareSessionMessageKind.Kick:
                    if (!TryGetAuthorityPlayerId(out ushort kickAuthorityPlayerId) ||
                        playerID != kickAuthorityPlayerId)
                    {
                        BasisDebug.LogWarning($"Rejected KoboldKare kick message from non-authority player {playerID}.");
                        return;
                    }
                    Kicked?.Invoke(message.Text);
                    BeginKickDisconnect();
                    break;
            }
        }

        public override void OnPlayerJoined(BasisNetworkPlayer player)
        {
            base.OnPlayerJoined(player);
            if (player == null)
            {
                return;
            }
            PlayerJoined?.Invoke(player.playerId, player.displayName);
            if (IsWorldAuthority && HasState)
            {
                SendStateTo(player.playerId);
            }
        }

        public override void OnPlayerLeft(BasisNetworkPlayer player)
        {
            base.OnPlayerLeft(player);
            if (player == null)
            {
                return;
            }
            PlayerLeft?.Invoke(player.playerId, player.displayName);
        }

        private async void BeginKickDisconnect()
        {
            try
            {
                await KoboldKareConnectionService.DisconnectAsync();
            }
            catch (Exception exception)
            {
                if (this != null)
                {
                    BasisDebug.LogError($"KoboldKare disconnect after kick failed: {exception}");
                }
            }
        }

        private void SynchronizeStateForRole()
        {
            if (!HasNetworkID || !BasisNetworkConnection.LocalPlayerIsConnected)
            {
                return;
            }

            if (IsWorldAuthority)
            {
                stateRequestSent = false;
                if (HasState)
                {
                    BroadcastState();
                }
                return;
            }

            if (TryGetAuthorityPlayerId(out ushort authorityPlayerId))
            {
                SendCustomNetworkEvent(
                    KoboldKareSessionProtocol.EncodeStateRequest(),
                    DeliveryMethod.ReliableOrdered,
                    new[] { authorityPlayerId });
                stateRequestSent = true;
            }
        }

        private bool CanMutateAuthorityState()
        {
            if (!BasisNetworkConnection.LocalPlayerIsConnected)
            {
                // Single-player/offline mode still needs one authoritative local session state.
                return true;
            }
            return IsWorldAuthority;
        }

        private void BroadcastState()
        {
            if (!HasState || !HasNetworkID || !BasisNetworkConnection.LocalPlayerIsConnected)
            {
                return;
            }
            SendCustomNetworkEvent(
                KoboldKareSessionProtocol.EncodeState(state),
                DeliveryMethod.ReliableOrdered);
        }

        private void SendStateTo(ushort playerId)
        {
            if (!HasState || !HasNetworkID || playerId == 0)
            {
                return;
            }
            SendCustomNetworkEvent(
                KoboldKareSessionProtocol.EncodeState(state),
                DeliveryMethod.ReliableOrdered,
                new[] { playerId });
        }

        private void ApplyRemoteState(in KoboldKareSessionState incoming)
        {
            if (HasState && !IsRevisionNewer(incoming.Revision, state.Revision))
            {
                return;
            }
            state = incoming;
            stateRequestSent = false;
            StateChanged?.Invoke(state);
        }

        private bool TryGetAuthorityPlayerId(out ushort playerId)
        {
            KoboldKareNetworkWorld world = KoboldKareNetworkWorld.Instance;
            if (world != null && world.TryGetWorldAuthorityPlayerId(out playerId))
            {
                return true;
            }
            playerId = 0;
            return false;
        }

        private static bool IsRevisionNewer(uint revision, uint previous)
        {
            return revision != previous && unchecked(revision - previous) < 0x80000000U;
        }
    }
}
