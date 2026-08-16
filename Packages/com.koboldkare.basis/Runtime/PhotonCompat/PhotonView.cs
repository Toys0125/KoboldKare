using System;
using System.Collections.Generic;
using System.Reflection;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using KoboldKare.Basis.Networking;
using KoboldKare.Basis.PhotonCompat;
using Photon.Realtime;
using UnityEngine;

namespace Photon.Pun
{
    /// <summary>
    /// Basis-backed compatibility implementation for the subset of PhotonView used by KoboldKare.
    /// Existing prefab serialization is intentionally kept close to PUN so the original PhotonView
    /// MonoScript GUID can be reused while the runtime implementation is replaced.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PhotonView : MonoBehaviour
    {
        public enum ObservableSearch
        {
            Manual,
            AutoFindActive,
            AutoFindAll,
        }

        private static readonly Dictionary<int, PhotonView> ViewsById = new Dictionary<int, PhotonView>();
        private static readonly object[] EmptyArguments = Array.Empty<object>();

        public ViewSynchronization Synchronization = ViewSynchronization.UnreliableOnChange;
        public OwnershipOption OwnershipTransfer = OwnershipOption.Fixed;
        public ObservableSearch observableSearch = ObservableSearch.Manual;
        public List<Component> ObservedComponents = new List<Component>();
        public int sceneViewId;

        private KoboldKareNetworkEntity entity;
        private byte subViewIndex;
        private int viewIdOverride;
        private bool initialized;
        private bool instantiateCallbacksInvoked;
        private bool staticSceneView;
        private int registeredViewId;
        private ushort lastOwnerId;
        private ushort lastControllerId;
        private uint observableSequence;
        private uint lastReceivedObservableSequence;
        private ushort lastObservableSender;
        private bool hasReceivedObservableSequence;
        private float nextSerializeTime;
        private readonly HashSet<ulong> appliedPersistentRpcIds = new HashSet<ulong>();

        public static IEnumerable<PhotonView> RegisteredViews
        {
            get
            {
                PhotonView[] snapshot = new PhotonView[ViewsById.Count];
                ViewsById.Values.CopyTo(snapshot, 0);
                return snapshot;
            }
        }

        public object[] InstantiationData { get; private set; } = Array.Empty<object>();

        public int ViewID
        {
            get
            {
                if (viewIdOverride > 0)
                {
                    return viewIdOverride;
                }
                if (staticSceneView && sceneViewId > 0)
                {
                    return sceneViewId;
                }
                if (entity != null &&
                    KoboldKareLegacyViewId.TryFromInstanceId(entity.InstanceId, subViewIndex, out int derivedViewId))
                {
                    return derivedViewId;
                }
                return sceneViewId > 0 ? sceneViewId : 0;
            }
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "Photon compatibility ViewID cannot be negative.");
                }
                if (viewIdOverride == value)
                {
                    return;
                }

                UnregisterView();
                viewIdOverride = value;
                RegisterView();
            }
        }

        public Player Owner
        {
            get
            {
                if (staticSceneView)
                {
                    return null;
                }
                ushort ownerId = ResolveOwnerId();
                return ownerId != 0 ? PhotonPlayerRegistry.GetOrCreate(ownerId) : null;
            }
        }

        public Player Controller
        {
            get
            {
                if (staticSceneView)
                {
                    return PhotonNetwork.MasterClient;
                }
                return Owner ?? PhotonNetwork.MasterClient;
            }
        }

        public bool IsMine
        {
            get
            {
                if (PhotonNetwork.OfflineMode)
                {
                    return true;
                }
                if (staticSceneView)
                {
                    return PhotonNetwork.IsMasterClient;
                }
                return entity != null && entity.IsMine;
            }
        }

        public bool AmController => IsMine;
        public bool IsRoomView => staticSceneView || sceneViewId > 0;
        public int CreatorActorNr => staticSceneView ? 0 : (entity?.InitialOwnerPlayerId ?? 0);
        public bool IsOwnerActive => Owner != null;

        public static PhotonView Get(Component component)
        {
            return component != null ? component.GetComponentInParent<PhotonView>() : null;
        }

        public static PhotonView Get(GameObject gameObject)
        {
            return gameObject != null ? gameObject.GetComponentInParent<PhotonView>() : null;
        }

        public static PhotonView Find(int viewId)
        {
            return TryGetRegistered(viewId, out PhotonView view) ? view : null;
        }

        internal static bool TryGetRegistered(int viewId, out PhotonView view)
        {
            if (viewId > 0 && ViewsById.TryGetValue(viewId, out view) && view != null)
            {
                return true;
            }
            view = null;
            return false;
        }

        private void Start()
        {
            InitializeFromEntity();
        }

        private void Update()
        {
            if (!initialized)
            {
                InitializeFromEntity();
                if (!initialized)
                {
                    return;
                }
            }

            UpdateOwnershipCallbacks();
            SerializeObservedComponentsIfDue();
        }

        private void OnDestroy()
        {
            UnregisterView();
            if (entity != null)
            {
                entity.ApplicationMessageReceived -= OnEntityApplicationMessage;
            }
            KoboldKareNetworkWorld world = KoboldKareNetworkWorld.Instance;
            if (world != null)
            {
                world.PersistentApplicationStateSnapshotReceived -= OnPersistentApplicationStateSnapshotReceived;
            }
        }

        internal void InitializeFromEntity()
        {
            if (initialized)
            {
                return;
            }

            entity = GetComponentInParent<KoboldKareNetworkEntity>();
            if (entity == null)
            {
                entity = CreateStaticSceneEntity();
                staticSceneView = entity != null;
            }
            else
            {
                staticSceneView = sceneViewId > 0 && entity.gameObject == gameObject && entity.PrefabId.StartsWith("scene/", StringComparison.Ordinal);
            }

            if (entity == null || !entity.IsConfigured)
            {
                return;
            }

            DetermineSubViewIndex();
            if (viewIdOverride == 0 && staticSceneView && sceneViewId > 0)
            {
                viewIdOverride = sceneViewId;
            }

            entity.ApplicationMessageReceived += OnEntityApplicationMessage;
            KoboldKareNetworkWorld world = KoboldKareNetworkWorld.Instance;
            if (world != null)
            {
                world.PersistentApplicationStateSnapshotReceived += OnPersistentApplicationStateSnapshotReceived;
            }

            if (!PhotonNetwork.TryDecodeInstantiationData(entity.InitialPayload, out object[] instantiationData))
            {
                BasisDebug.LogWarning($"Rejected malformed KoboldKare instantiation data for Photon-compatible view on '{name}'.");
                instantiationData = Array.Empty<object>();
            }
            InstantiationData = instantiationData;

            if (ObservedComponents == null)
            {
                ObservedComponents = new List<Component>();
            }
            if (observableSearch != ObservableSearch.Manual && ObservedComponents.Count == 0)
            {
                FindObservables(true);
            }

            initialized = true;
            RegisterView();
            lastOwnerId = ResolveOwnerId();
            lastControllerId = ResolveControllerId();
            nextSerializeTime = Time.unscaledTime;

            if (!staticSceneView && subViewIndex == 0)
            {
                InvokeInstantiationCallbacks();
            }

            ApplyInitialPersistentApplicationState();
        }

        public void FindObservables(bool force = false)
        {
            if (!force && observableSearch == ObservableSearch.Manual)
            {
                return;
            }

            bool includeInactive = observableSearch == ObservableSearch.AutoFindAll || force;
            MonoBehaviour[] behaviours = GetComponentsInChildren<MonoBehaviour>(includeInactive);
            if (ObservedComponents == null)
            {
                ObservedComponents = new List<Component>();
            }
            ObservedComponents.Clear();
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour != null && behaviour != this && behaviour is IPunObservable)
                {
                    ObservedComponents.Add(behaviour);
                }
            }
        }

        public void RPC(string methodName, RpcTarget target, params object[] parameters)
        {
            InitializeFromEntity();
            if (!initialized)
            {
                BasisDebug.LogWarning($"Cannot invoke RPC '{methodName}' because Photon-compatible view '{name}' is not initialized.");
                return;
            }

            parameters ??= EmptyArguments;
            bool buffered = target == RpcTarget.AllBuffered ||
                            target == RpcTarget.OthersBuffered ||
                            target == RpcTarget.AllBufferedViaServer;
            ulong persistentMessageId = buffered ? CreatePersistentMessageId() : 0;
            byte[] payload;
            try
            {
                payload = PhotonCompatProtocol.EncodeRpc(
                    subViewIndex,
                    buffered,
                    persistentMessageId,
                    methodName,
                    parameters);
            }
            catch (Exception exception)
            {
                BasisDebug.LogError($"Failed to encode KoboldKare RPC '{methodName}' on view {ViewID}: {exception}");
                return;
            }

            if (buffered)
            {
                RecordPersistentMessage(payload);
            }

            bool invokeLocal = target == RpcTarget.All ||
                               target == RpcTarget.AllBuffered ||
                               target == RpcTarget.AllViaServer ||
                               target == RpcTarget.AllBufferedViaServer ||
                               (target == RpcTarget.MasterClient && PhotonNetwork.IsMasterClient);
            if (invokeLocal)
            {
                InvokeRpc(methodName, parameters, PhotonNetwork.LocalPlayer);
                if (buffered)
                {
                    appliedPersistentRpcIds.Add(persistentMessageId);
                }
            }

            if (PhotonNetwork.OfflineMode || entity == null || !entity.HasNetworkID || !BasisNetworkConnection.LocalPlayerIsConnected)
            {
                return;
            }

            ushort[] recipients = ResolveRecipients(target);
            if (target == RpcTarget.MasterClient && (recipients == null || recipients.Length == 0))
            {
                return;
            }

            entity.SendApplicationMessage(
                PhotonCompatProtocol.EntityRouteId,
                payload,
                DeliveryMethod.ReliableOrdered,
                recipients);
        }

        public void RequestOwnership()
        {
            InitializeFromEntity();
            if (!initialized || entity == null || IsMine)
            {
                return;
            }

            switch (OwnershipTransfer)
            {
                case OwnershipOption.Fixed:
                    BasisDebug.LogWarning($"Attempted to request fixed ownership for Photon-compatible view {ViewID}.");
                    return;
                case OwnershipOption.Takeover:
                    BeginTakeOwnership();
                    return;
                case OwnershipOption.Request:
                {
                    Player controller = Controller;
                    if (controller == null || controller.ActorNumber <= 0 || controller.ActorNumber > ushort.MaxValue)
                    {
                        BasisDebug.LogWarning($"Cannot request ownership for view {ViewID}: no active controller is available.");
                        return;
                    }
                    entity.SendApplicationMessage(
                        PhotonCompatProtocol.EntityRouteId,
                        PhotonCompatProtocol.EncodeOwnershipRequest(subViewIndex),
                        DeliveryMethod.ReliableOrdered,
                        new[] { (ushort)controller.ActorNumber });
                    return;
                }
            }
        }

        public void TransferOwnership(Player newOwner)
        {
            if (newOwner == null)
            {
                BasisDebug.LogWarning($"Cannot transfer Photon-compatible view {ViewID} to a null player.");
                return;
            }
            TransferOwnership(newOwner.ActorNumber);
        }

        public void TransferOwnership(int newOwnerId)
        {
            InitializeFromEntity();
            if (!initialized || entity == null || newOwnerId <= 0 || newOwnerId > ushort.MaxValue)
            {
                return;
            }
            BeginTransferOwnership((ushort)newOwnerId);
        }

        private KoboldKareNetworkEntity CreateStaticSceneEntity()
        {
            if (!gameObject.scene.IsValid())
            {
                return null;
            }

            int stableViewId = sceneViewId > 0 ? sceneViewId : ComputeStableSceneViewId();
            if (stableViewId <= 0)
            {
                return null;
            }

            string instanceId = BuildStaticInstanceId(stableViewId);
            KoboldKareNetworkEntity created = gameObject.AddComponent<KoboldKareNetworkEntity>();
            created.Configure(new KoboldKareSpawnDescriptor
            {
                InstanceId = instanceId,
                PrefabId = $"scene/{gameObject.scene.name}/{stableViewId}",
                OwnerPlayerId = 0,
                Position = transform.position,
                Rotation = transform.rotation,
                Scale = transform.localScale,
                InitialPayload = Array.Empty<byte>(),
                PersistentApplicationPayload = Array.Empty<byte>(),
            });
            return created;
        }

        private void DetermineSubViewIndex()
        {
            PhotonView[] views = entity.GetComponentsInChildren<PhotonView>(true);
            int index = Array.IndexOf(views, this);
            if (index < 0)
            {
                index = 0;
            }
            if (index > byte.MaxValue)
            {
                throw new InvalidOperationException($"KoboldKare entity '{entity.InstanceId}' contains more than 256 Photon-compatible views.");
            }
            subViewIndex = (byte)index;
        }

        private void RegisterView()
        {
            int viewId = ViewID;
            if (!initialized || viewId <= 0)
            {
                return;
            }

            if (ViewsById.TryGetValue(viewId, out PhotonView existing) && existing != null && existing != this)
            {
                BasisDebug.LogError(
                    $"Photon-compatible ViewID collision {viewId} between '{existing.name}' and '{name}'. The newer view will remain unregistered.");
                registeredViewId = 0;
                return;
            }

            ViewsById[viewId] = this;
            registeredViewId = viewId;
        }

        private void UnregisterView()
        {
            if (registeredViewId <= 0)
            {
                return;
            }
            if (ViewsById.TryGetValue(registeredViewId, out PhotonView registered) && registered == this)
            {
                ViewsById.Remove(registeredViewId);
            }
            registeredViewId = 0;
        }

        private void OnEntityApplicationMessage(
            ushort playerId,
            ushort routeId,
            byte[] payload,
            DeliveryMethod deliveryMethod)
        {
            if (routeId != PhotonCompatProtocol.EntityRouteId ||
                !PhotonCompatProtocol.TryGetKind(payload, out PhotonCompatMessageKind kind))
            {
                return;
            }

            switch (kind)
            {
                case PhotonCompatMessageKind.Rpc:
                    HandleRpcMessage(playerId, payload);
                    break;
                case PhotonCompatMessageKind.Observable:
                    HandleObservableMessage(playerId, payload);
                    break;
                case PhotonCompatMessageKind.OwnershipRequest:
                    HandleOwnershipRequest(playerId, payload);
                    break;
            }
        }

        private void HandleRpcMessage(ushort playerId, byte[] payload)
        {
            if (!PhotonCompatProtocol.TryDecodeRpc(payload, out PhotonCompatRpcMessage message) ||
                message.SubViewIndex != subViewIndex)
            {
                return;
            }

            if (message.Buffered)
            {
                if (!appliedPersistentRpcIds.Add(message.PersistentMessageId))
                {
                    return;
                }
                RecordPersistentMessage(payload);
            }

            InvokeRpc(message.MethodName, message.Arguments, PhotonPlayerRegistry.GetOrCreate(playerId));
        }

        private void HandleObservableMessage(ushort playerId, byte[] payload)
        {
            if (!PhotonCompatProtocol.TryDecodeObservable(payload, out PhotonCompatObservableMessage message) ||
                message.SubViewIndex != subViewIndex)
            {
                return;
            }

            ushort controllerId = ResolveControllerId();
            if (controllerId == 0 || playerId != controllerId)
            {
                BasisDebug.LogWarning($"Rejected observable state for view {ViewID} from non-controller player {playerId}.");
                return;
            }

            if (hasReceivedObservableSequence &&
                lastObservableSender == playerId &&
                !IsSequenceNewer(message.Sequence, lastReceivedObservableSequence))
            {
                return;
            }

            lastObservableSender = playerId;
            lastReceivedObservableSequence = message.Sequence;
            hasReceivedObservableSequence = true;
            InvokeObservableRead(message.Values, PhotonPlayerRegistry.GetOrCreate(playerId));
        }

        private void HandleOwnershipRequest(ushort playerId, byte[] payload)
        {
            if (!PhotonCompatProtocol.TryDecodeOwnershipRequest(payload, out byte requestedSubView) ||
                requestedSubView != subViewIndex ||
                OwnershipTransfer != OwnershipOption.Request ||
                !IsMine)
            {
                return;
            }

            PhotonNetwork.DispatchOwnershipRequest(this, PhotonPlayerRegistry.GetOrCreate(playerId));
        }

        private void SerializeObservedComponentsIfDue()
        {
            if (!IsMine ||
                PhotonNetwork.OfflineMode ||
                entity == null ||
                !entity.HasNetworkID ||
                !BasisNetworkConnection.LocalPlayerIsConnected ||
                Synchronization == ViewSynchronization.Off ||
                ObservedComponents == null ||
                ObservedComponents.Count == 0)
            {
                return;
            }

            int rate = Mathf.Clamp(PhotonNetwork.SerializationRate, 1, 120);
            float now = Time.unscaledTime;
            if (now < nextSerializeTime)
            {
                return;
            }
            nextSerializeTime = now + 1f / rate;

            PhotonStream stream = new PhotonStream(true, null);
            PhotonMessageInfo info = new PhotonMessageInfo(PhotonNetwork.LocalPlayer, this);
            for (int i = 0; i < ObservedComponents.Count; i++)
            {
                if (ObservedComponents[i] is not IPunObservable observable)
                {
                    continue;
                }
                try
                {
                    observable.OnPhotonSerializeView(stream, info);
                }
                catch (Exception exception)
                {
                    BasisDebug.LogError($"OnPhotonSerializeView write failed on '{ObservedComponents[i]}': {exception}");
                    return;
                }
            }

            if (stream.Count == 0)
            {
                return;
            }

            byte[] payload;
            try
            {
                payload = PhotonCompatProtocol.EncodeObservable(subViewIndex, ++observableSequence, stream.ToArray());
            }
            catch (Exception exception)
            {
                BasisDebug.LogError($"Failed to encode observable state for view {ViewID}: {exception}");
                return;
            }

            if (Synchronization == ViewSynchronization.ReliableDeltaCompressed)
            {
                entity.SendApplicationMessage(
                    PhotonCompatProtocol.EntityRouteId,
                    payload,
                    DeliveryMethod.ReliableOrdered);
            }
            else
            {
                entity.SendApplicationMessageDirect(
                    PhotonCompatProtocol.EntityRouteId,
                    payload,
                    DeliveryMethod.Unreliable,
                    null,
                    true);
            }
        }

        private void InvokeObservableRead(object[] values, Player sender)
        {
            PhotonStream stream = new PhotonStream(false, values);
            PhotonMessageInfo info = new PhotonMessageInfo(sender, this);
            for (int i = 0; i < ObservedComponents.Count; i++)
            {
                if (ObservedComponents[i] is not IPunObservable observable)
                {
                    continue;
                }
                try
                {
                    observable.OnPhotonSerializeView(stream, info);
                }
                catch (Exception exception)
                {
                    BasisDebug.LogError($"OnPhotonSerializeView read failed on '{ObservedComponents[i]}': {exception}");
                    return;
                }
            }
        }

        private void InvokeRpc(string methodName, object[] arguments, Player sender)
        {
            MonoBehaviour[] behaviours = GetComponents<MonoBehaviour>();
            bool invoked = false;
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null || behaviour == this)
                {
                    continue;
                }

                MethodInfo[] methods = behaviour.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (int m = 0; m < methods.Length; m++)
                {
                    MethodInfo method = methods[m];
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal) ||
                        method.GetCustomAttribute<PunRPC>(true) == null)
                    {
                        continue;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    if (!TryBuildInvocationArguments(parameters, arguments, sender, out object[] invokeArguments))
                    {
                        continue;
                    }

                    try
                    {
                        method.Invoke(behaviour, invokeArguments);
                        invoked = true;
                    }
                    catch (TargetInvocationException exception)
                    {
                        BasisDebug.LogError(
                            $"KoboldKare RPC '{methodName}' on '{behaviour.GetType().Name}' threw: {exception.InnerException ?? exception}");
                    }
                    catch (Exception exception)
                    {
                        BasisDebug.LogError($"KoboldKare RPC '{methodName}' invocation failed: {exception}");
                    }
                }
            }

            if (!invoked)
            {
                BasisDebug.LogWarning($"No matching [PunRPC] method '{methodName}' was found on Photon-compatible view {ViewID} ('{name}').");
            }
        }

        private bool TryBuildInvocationArguments(
            ParameterInfo[] parameters,
            object[] supplied,
            Player sender,
            out object[] invocationArguments)
        {
            supplied ??= EmptyArguments;
            bool hasMessageInfo = parameters.Length == supplied.Length + 1 &&
                                  parameters.Length > 0 &&
                                  parameters[^1].ParameterType == typeof(PhotonMessageInfo);
            if ((!hasMessageInfo && parameters.Length != supplied.Length) ||
                (hasMessageInfo && parameters.Length != supplied.Length + 1))
            {
                invocationArguments = null;
                return false;
            }

            invocationArguments = new object[parameters.Length];
            for (int i = 0; i < supplied.Length; i++)
            {
                object value = supplied[i];
                Type targetType = parameters[i].ParameterType;
                if (value == null)
                {
                    if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                    {
                        return false;
                    }
                    invocationArguments[i] = null;
                    continue;
                }
                if (!targetType.IsInstanceOfType(value))
                {
                    return false;
                }
                invocationArguments[i] = value;
            }

            if (hasMessageInfo)
            {
                invocationArguments[^1] = new PhotonMessageInfo(sender, this);
            }
            return true;
        }

        private void InvokeInstantiationCallbacks()
        {
            if (instantiateCallbacksInvoked)
            {
                return;
            }
            instantiateCallbacksInvoked = true;

            Player sender = entity.InitialOwnerPlayerId != 0
                ? PhotonPlayerRegistry.GetOrCreate(entity.InitialOwnerPlayerId)
                : PhotonNetwork.MasterClient;
            PhotonMessageInfo info = new PhotonMessageInfo(sender, this);
            MonoBehaviour[] behaviours = entity.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is not IPunInstantiateMagicCallback callback)
                {
                    continue;
                }
                try
                {
                    callback.OnPhotonInstantiate(info);
                }
                catch (Exception exception)
                {
                    BasisDebug.LogError($"OnPhotonInstantiate failed on '{behaviours[i]}': {exception}");
                }
            }
        }

        private void ApplyInitialPersistentApplicationState()
        {
            byte[] history = entity.PersistentApplicationPayload.ToArray();
            if (history.Length == 0 &&
                KoboldKareNetworkWorld.Instance != null &&
                KoboldKareNetworkWorld.Instance.TryGetPersistentApplicationState(entity.InstanceId, out byte[] worldHistory))
            {
                history = worldHistory;
            }
            ApplyPersistentApplicationHistory(history);
        }

        private void OnPersistentApplicationStateSnapshotReceived(string instanceId, byte[] history)
        {
            if (entity == null || !string.Equals(entity.InstanceId, instanceId, StringComparison.Ordinal))
            {
                return;
            }
            ApplyPersistentApplicationHistory(history);
        }

        private void ApplyPersistentApplicationHistory(byte[] history)
        {
            if (!KoboldKarePersistentMessageHistory.TryDecode(history, out List<KoboldKarePersistentMessage> messages))
            {
                if (history != null && history.Length > 0)
                {
                    BasisDebug.LogWarning($"Rejected malformed persistent application history for view {ViewID}.");
                }
                return;
            }

            for (int i = 0; i < messages.Count; i++)
            {
                KoboldKarePersistentMessage persistentMessage = messages[i];
                if (persistentMessage.RouteId != PhotonCompatProtocol.EntityRouteId ||
                    !PhotonCompatProtocol.TryDecodeRpc(persistentMessage.Payload, out PhotonCompatRpcMessage rpc) ||
                    !rpc.Buffered ||
                    rpc.SubViewIndex != subViewIndex ||
                    !appliedPersistentRpcIds.Add(rpc.PersistentMessageId))
                {
                    continue;
                }

                Player sender = entity.InitialOwnerPlayerId != 0
                    ? PhotonPlayerRegistry.GetOrCreate(entity.InitialOwnerPlayerId)
                    : PhotonNetwork.MasterClient;
                InvokeRpc(rpc.MethodName, rpc.Arguments, sender);
            }
        }

        private void RecordPersistentMessage(byte[] payload)
        {
            KoboldKareNetworkWorld world = KoboldKareNetworkWorld.Instance;
            if (world != null && !world.RecordPersistentApplicationMessage(
                    entity.InstanceId,
                    PhotonCompatProtocol.EntityRouteId,
                    payload))
            {
                BasisDebug.LogWarning($"Could not record buffered RPC state for view {ViewID}.");
            }
        }

        private ushort[] ResolveRecipients(RpcTarget target)
        {
            if (target != RpcTarget.MasterClient)
            {
                return null;
            }

            Player master = PhotonNetwork.MasterClient;
            if (master == null || master.ActorNumber <= 0 || master.ActorNumber > ushort.MaxValue)
            {
                return Array.Empty<ushort>();
            }
            return new[] { (ushort)master.ActorNumber };
        }

        private async void BeginTakeOwnership()
        {
            try
            {
                BasisOwnershipResult result = await entity.TakeOwnershipAsync();
                if (this != null && !result.Success)
                {
                    BasisDebug.LogWarning($"Failed to take ownership of Photon-compatible view {ViewID}.");
                }
            }
            catch (Exception exception)
            {
                if (this != null)
                {
                    BasisDebug.LogError($"Ownership takeover failed for view {ViewID}: {exception}");
                }
            }
        }

        private async void BeginTransferOwnership(ushort playerId)
        {
            try
            {
                BasisOwnershipResult result = await entity.TransferOwnershipToAsync(playerId);
                if (this != null && !result.Success)
                {
                    BasisDebug.LogWarning($"Failed to transfer Photon-compatible view {ViewID} to player {playerId}.");
                }
            }
            catch (Exception exception)
            {
                if (this != null)
                {
                    BasisDebug.LogError($"Ownership transfer failed for view {ViewID}: {exception}");
                }
            }
        }

        private void UpdateOwnershipCallbacks()
        {
            ushort ownerId = ResolveOwnerId();
            ushort controllerId = ResolveControllerId();
            if (ownerId == lastOwnerId && controllerId == lastControllerId)
            {
                return;
            }

            Player previousOwner = lastOwnerId != 0 ? PhotonPlayerRegistry.GetOrCreate(lastOwnerId) : null;
            Player newOwner = ownerId != 0 ? PhotonPlayerRegistry.GetOrCreate(ownerId) : null;
            Player previousController = lastControllerId != 0 ? PhotonPlayerRegistry.GetOrCreate(lastControllerId) : null;
            Player newController = controllerId != 0 ? PhotonPlayerRegistry.GetOrCreate(controllerId) : null;

            MonoBehaviour[] behaviours = GetComponents<MonoBehaviour>();
            if (ownerId != lastOwnerId)
            {
                for (int i = 0; i < behaviours.Length; i++)
                {
                    if (behaviours[i] is IOnPhotonViewOwnerChange ownerChange)
                    {
                        try
                        {
                            ownerChange.OnOwnerChange(newOwner, previousOwner);
                        }
                        catch (Exception exception)
                        {
                            BasisDebug.LogError($"Photon-compatible owner callback failed: {exception}");
                        }
                    }
                }
                PhotonNetwork.DispatchOwnershipTransferred(this, previousOwner);
            }

            if (controllerId != lastControllerId)
            {
                for (int i = 0; i < behaviours.Length; i++)
                {
                    if (behaviours[i] is IOnPhotonViewControllerChange controllerChange)
                    {
                        try
                        {
                            controllerChange.OnControllerChange(newController, previousController);
                        }
                        catch (Exception exception)
                        {
                            BasisDebug.LogError($"Photon-compatible controller callback failed: {exception}");
                        }
                    }
                }
            }

            lastOwnerId = ownerId;
            lastControllerId = controllerId;
            if (lastObservableSender != controllerId)
            {
                hasReceivedObservableSequence = false;
                lastObservableSender = controllerId;
            }
        }

        private ushort ResolveOwnerId()
        {
            if (entity == null || staticSceneView)
            {
                return 0;
            }
            ushort ownerId = entity.OwnerPlayerId;
            return ownerId != 0 ? ownerId : entity.InitialOwnerPlayerId;
        }

        private ushort ResolveControllerId()
        {
            if (staticSceneView)
            {
                Player master = PhotonNetwork.MasterClient;
                return master != null && master.ActorNumber > 0 && master.ActorNumber <= ushort.MaxValue
                    ? (ushort)master.ActorNumber
                    : (ushort)0;
            }
            ushort ownerId = ResolveOwnerId();
            if (ownerId != 0)
            {
                return ownerId;
            }
            Player fallbackMaster = PhotonNetwork.MasterClient;
            return fallbackMaster != null && fallbackMaster.ActorNumber > 0 && fallbackMaster.ActorNumber <= ushort.MaxValue
                ? (ushort)fallbackMaster.ActorNumber
                : (ushort)0;
        }

        private int ComputeStableSceneViewId()
        {
            string key = gameObject.scene.path + "|" + GetHierarchyPath(transform);
            uint hash = 2166136261U;
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                hash ^= (byte)c;
                hash *= 16777619U;
                hash ^= (byte)(c >> 8);
                hash *= 16777619U;
            }
            int stable = (int)(hash & 0x7fffffffU);
            return stable == 0 ? 1 : stable;
        }

        private string BuildStaticInstanceId(int stableViewId)
        {
            string key = gameObject.scene.path + "|" + GetHierarchyPath(transform) + "|" + stableViewId;
            uint a = HashString(key, 2166136261U);
            uint b = HashString(key, a ^ 0x9e3779b9U);
            uint c = HashString(key, b ^ 0x85ebca6bU);
            return ((uint)stableViewId).ToString("x8") + a.ToString("x8") + b.ToString("x8") + c.ToString("x8");
        }

        private static uint HashString(string value, uint seed)
        {
            uint hash = seed;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                hash ^= (byte)c;
                hash *= 16777619U;
                hash ^= (byte)(c >> 8);
                hash *= 16777619U;
            }
            return hash;
        }

        private static string GetHierarchyPath(Transform target)
        {
            string path = target.name + "[" + target.GetSiblingIndex() + "]";
            Transform parent = target.parent;
            while (parent != null)
            {
                path = parent.name + "[" + parent.GetSiblingIndex() + "]/" + path;
                parent = parent.parent;
            }
            return path;
        }

        private static ulong CreatePersistentMessageId()
        {
            byte[] bytes = Guid.NewGuid().ToByteArray();
            ulong value = BitConverter.ToUInt64(bytes, 0);
            return value == 0 ? 1UL : value;
        }

        private static bool IsSequenceNewer(uint sequence, uint previous)
        {
            return sequence != previous && unchecked(sequence - previous) < 0x80000000U;
        }
    }
}
