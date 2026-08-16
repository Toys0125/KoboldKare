using System;
using System.Threading;
using System.Threading.Tasks;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;

namespace KoboldKare.Basis.Networking
{
    public readonly struct KoboldKareConnectionOptions
    {
        public readonly string Address;
        public readonly ushort Port;
        public readonly string Password;
        public readonly string DisplayName;
        public readonly string NetworkStackId;
        public readonly bool Host;
        public readonly string ServerName;
        public readonly int PeerLimit;

        public KoboldKareConnectionOptions(
            string address,
            ushort port,
            string password,
            string displayName,
            bool host = false,
            string serverName = null,
            int peerLimit = 0,
            string networkStackId = null)
        {
            Address = address;
            Port = port;
            Password = password;
            DisplayName = displayName;
            Host = host;
            ServerName = serverName;
            PeerLimit = peerLimit;
            NetworkStackId = networkStackId;
        }
    }

    /// <summary>
    /// KoboldKare-specific Basis connection orchestration. Unlike BasisConnectionService this does
    /// not load the default Basis world bundle; KoboldKare owns its own map loading lifecycle.
    /// Callers must tear down the current KoboldKare gameplay scene before reconnecting because
    /// BasisNetworkLifeCycle.Destroy clears the framework's global player callbacks.
    /// </summary>
    public static class KoboldKareConnectionService
    {
        public const ushort DefaultPort = 4296;
        public const int DefaultConnectTimeoutMilliseconds = 15000;
        public const int DefaultDisconnectTimeoutMilliseconds = 5000;

        private static readonly SemaphoreSlim ConnectionGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Game-side persistent BasisNetworkBehaviour roots must be synchronously removed before
        /// BasisNetworkLifeCycle.Destroy clears the framework's static callback tables.
        /// </summary>
        public static event Action NetworkRuntimeTeardownRequested;

        /// <summary>
        /// Raised after BasisNetworkLifeCycle.Initialize so game-side network runtime roots can be
        /// recreated and subscribe before the next connection attempt starts.
        /// </summary>
        public static event Action NetworkRuntimeReinitializeRequested;

        public static async Task ConnectAsync(
            KoboldKareConnectionOptions options,
            CancellationToken cancellationToken = default,
            int timeoutMilliseconds = DefaultConnectTimeoutMilliseconds)
        {
            ValidateOptions(options);
            if (timeoutMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            }

            await ConnectionGate.WaitAsync(cancellationToken);
            try
            {
                if (BasisNetworkConnection.LocalPlayerIsConnected || BasisNetworkConnection.LocalPlayerPeer != null)
                {
                    await DisconnectInternalAsync(
                        reinitialize: true,
                        cancellationToken,
                        DefaultDisconnectTimeoutMilliseconds);
                }
                else if (!BasisNetworkManagement.IsInitialized)
                {
                    BasisNetworkLifeCycle.Initialize();
                    NetworkRuntimeReinitializeRequested?.Invoke();
                }

                BasisLocalPlayer localPlayer = BasisLocalPlayer.Instance;
                if (localPlayer == null)
                {
                    throw new InvalidOperationException("BasisLocalPlayer is not initialized.");
                }

                localPlayer.DisplayName = options.DisplayName.Trim();
                localPlayer.SetSafeDisplayname();

                BasisNetworkManagement.Port = options.Port == 0 ? DefaultPort : options.Port;
                BasisNetworkManagement.Ip = options.Host ? "localhost" : options.Address.Trim();
                BasisNetworkManagement.Password = options.Password ?? string.Empty;
                BasisNetworkManagement.IsHostMode = options.Host;
                BasisNetworkManagement.NetworkStackId = string.IsNullOrWhiteSpace(options.NetworkStackId)
                    ? BasisNetworkStackRegistry.DefaultId
                    : options.NetworkStackId.Trim();

                if (options.Host)
                {
                    BasisNetworkManagement.HostServerName = string.IsNullOrWhiteSpace(options.ServerName)
                        ? "KoboldKare"
                        : options.ServerName.Trim();
                    BasisNetworkManagement.HostPeerLimit = options.PeerLimit <= 0
                        ? ushort.MaxValue
                        : Math.Min(options.PeerLimit, ushort.MaxValue);
                    BasisNetworkManagement.HostUseAuth = true;
                    BasisNetworkManagement.HostWorldsLocked = true;
                }

                Task connectedTask = WaitForLocalPlayerJoinedAsync(cancellationToken, timeoutMilliseconds);
                BasisNetworkManagement.Connect();
                await connectedTask;
            }
            finally
            {
                ConnectionGate.Release();
            }
        }

        public static async Task DisconnectAsync(
            CancellationToken cancellationToken = default,
            bool reinitialize = true,
            int timeoutMilliseconds = DefaultDisconnectTimeoutMilliseconds)
        {
            if (timeoutMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            }

            await ConnectionGate.WaitAsync(cancellationToken);
            try
            {
                await DisconnectInternalAsync(reinitialize, cancellationToken, timeoutMilliseconds);
            }
            finally
            {
                ConnectionGate.Release();
            }
        }

        private static async Task DisconnectInternalAsync(
            bool reinitialize,
            CancellationToken cancellationToken,
            int timeoutMilliseconds)
        {
            bool hadPeer = BasisNetworkConnection.LocalPlayerPeer != null;
            Task rebootTask = Task.CompletedTask;
            CancellationTokenSource rebootTimeout = null;

            if (hadPeer)
            {
                rebootTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                rebootTimeout.CancelAfter(timeoutMilliseconds);
                rebootTask = BasisNetworkConnection.WaitForRebootCompleteAsync(rebootTimeout.Token);
            }

            try
            {
                if (BasisNetworkManagement.IsInitialized || BasisNetworkManagement.NetworkRunning)
                {
                    NetworkRuntimeTeardownRequested?.Invoke();
                    Photon.Pun.PhotonNetwork.ResetCompatibilitySession();
                    await BasisNetworkLifeCycle.Destroy();
                }

                if (hadPeer)
                {
                    try
                    {
                        await rebootTask;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Destroy already performed the complete synchronous cleanup. A few network
                        // backends may not surface a final disconnect callback after explicit teardown.
                        BasisDebug.LogWarning(
                            "KoboldKare timed out waiting for the Basis disconnect callback after cleanup; continuing with the destroyed network state.");
                    }
                }
            }
            finally
            {
                rebootTimeout?.Dispose();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (reinitialize && !BasisNetworkManagement.IsInitialized)
            {
                BasisNetworkLifeCycle.Initialize();
                NetworkRuntimeReinitializeRequested?.Invoke();
            }
        }

        private static Task WaitForLocalPlayerJoinedAsync(
            CancellationToken cancellationToken,
            int timeoutMilliseconds)
        {
            if (BasisNetworkConnection.LocalPlayerIsConnected &&
                BasisNetworkConnection.TryGetLocalPlayerID(out _))
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMilliseconds);

            void Joined(BasisNetworkPlayer networkedPlayer, BasisLocalPlayer localPlayer)
            {
                BasisNetworkPlayer.OnLocalPlayerJoined -= Joined;
                completion.TrySetResult(true);
            }

            BasisNetworkPlayer.OnLocalPlayerJoined += Joined;
            CancellationTokenRegistration registration = timeout.Token.Register(() =>
            {
                BasisNetworkPlayer.OnLocalPlayerJoined -= Joined;
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                else
                {
                    completion.TrySetException(
                        new TimeoutException($"Basis connection did not complete within {timeoutMilliseconds} ms."));
                }
            });

            _ = completion.Task.ContinueWith(
                _ =>
                {
                    registration.Dispose();
                    timeout.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return completion.Task;
        }

        private static void ValidateOptions(in KoboldKareConnectionOptions options)
        {
            if (!options.Host && string.IsNullOrWhiteSpace(options.Address))
            {
                throw new ArgumentException("A server address is required when joining a KoboldKare server.", nameof(options));
            }
            if (string.IsNullOrWhiteSpace(options.DisplayName))
            {
                throw new ArgumentException("A display name is required.", nameof(options));
            }
            if (options.PeerLimit < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Peer limit cannot be negative.");
            }
        }
    }
}
