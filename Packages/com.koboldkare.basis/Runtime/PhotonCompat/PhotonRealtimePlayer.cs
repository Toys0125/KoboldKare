using System;
using System.Collections.Generic;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;

namespace Photon.Realtime
{
    /// <summary>
    /// Minimal PUN Player compatibility object backed by a Basis player id.
    /// TagObject intentionally remains client-local, matching how KoboldKare uses it to associate
    /// each network player with its spawned Kobold on that client.
    /// </summary>
    public sealed class Player : IEquatable<Player>
    {
        internal Player(ushort basisPlayerId)
        {
            BasisPlayerId = basisPlayerId;
        }

        internal ushort BasisPlayerId { get; }
        public int ActorNumber => BasisPlayerId;
        public object TagObject { get; set; }

        public string NickName
        {
            get
            {
                if (BasisNetworkPlayers.Players.TryGetValue(BasisPlayerId, out BasisNetworkPlayer player) && player != null)
                {
                    return player.displayName;
                }
                return $"Player {BasisPlayerId}";
            }
        }

        public bool Equals(Player other) => other != null && other.BasisPlayerId == BasisPlayerId;
        public override bool Equals(object obj) => obj is Player other && Equals(other);
        public override int GetHashCode() => BasisPlayerId;
        public override string ToString() => $"{NickName} ({ActorNumber})";

        public static bool operator ==(Player left, Player right) => ReferenceEquals(left, right) || (left?.Equals(right) ?? false);
        public static bool operator !=(Player left, Player right) => !(left == right);
    }
}

namespace KoboldKare.Basis.PhotonCompat
{
    using Photon.Realtime;

    internal static class PhotonPlayerRegistry
    {
        private static readonly Dictionary<ushort, Player> Players = new Dictionary<ushort, Player>();

        public static Player GetOrCreate(ushort playerId)
        {
            if (!Players.TryGetValue(playerId, out Player player))
            {
                player = new Player(playerId);
                Players.Add(playerId, player);
            }
            return player;
        }

        public static Player LocalPlayer
        {
            get
            {
                return BasisNetworkConnection.TryGetLocalPlayerID(out ushort playerId)
                    ? GetOrCreate(playerId)
                    : null;
            }
        }

        public static void ResetSessionState()
        {
            Players.Clear();
        }

        public static Player[] GetPlayerList()
        {
            var ids = new List<ushort>(BasisNetworkPlayers.Players.Keys);
            if (BasisNetworkConnection.TryGetLocalPlayerID(out ushort localPlayerId) && !ids.Contains(localPlayerId))
            {
                ids.Add(localPlayerId);
            }
            ids.Sort();

            Player[] result = new Player[ids.Count];
            for (int i = 0; i < ids.Count; i++)
            {
                result[i] = GetOrCreate(ids[i]);
            }
            return result;
        }
    }
}
