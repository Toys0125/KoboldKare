using System;
using System.Collections.Generic;
using Photon.Realtime;

namespace Photon.Pun
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class PunRPC : Attribute
    {
    }

    public enum RpcTarget : byte
    {
        All = 0,
        Others = 1,
        MasterClient = 2,
        AllBuffered = 3,
        OthersBuffered = 4,
        AllViaServer = 5,
        AllBufferedViaServer = 6,
    }

    public enum OwnershipOption : byte
    {
        Fixed = 0,
        Takeover = 1,
        Request = 2,
    }

    public enum ViewSynchronization : byte
    {
        Off = 0,
        ReliableDeltaCompressed = 1,
        Unreliable = 2,
        UnreliableOnChange = 3,
    }

    public interface IPunObservable
    {
        void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info);
    }

    public interface IPunOwnershipCallbacks
    {
        void OnOwnershipRequest(PhotonView targetView, Player requestingPlayer);
        void OnOwnershipTransfered(PhotonView targetView, Player previousOwner);
        void OnOwnershipTransferFailed(PhotonView targetView, Player senderOfFailedRequest);
    }

    public interface IPunInstantiateMagicCallback
    {
        void OnPhotonInstantiate(PhotonMessageInfo info);
    }

    public interface IPhotonViewCallback
    {
    }

    public interface IOnPhotonViewPreNetDestroy : IPhotonViewCallback
    {
        void OnPreNetDestroy(PhotonView rootView);
    }

    public interface IOnPhotonViewOwnerChange : IPhotonViewCallback
    {
        void OnOwnerChange(Player newOwner, Player previousOwner);
    }

    public interface IOnPhotonViewControllerChange : IPhotonViewCallback
    {
        void OnControllerChange(Player newController, Player previousController);
    }

    public sealed class PhotonStream
    {
        private readonly List<object> writeData;
        private readonly object[] readData;
        private int readPosition;

        public PhotonStream(bool write, object[] incomingData)
        {
            IsWriting = write;
            if (write)
            {
                writeData = new List<object>(16);
                readData = Array.Empty<object>();
            }
            else
            {
                writeData = null;
                readData = incomingData ?? Array.Empty<object>();
            }
        }

        public bool IsWriting { get; }
        public bool IsReading => !IsWriting;
        public int Count => IsWriting ? writeData.Count : readData.Length;

        public void SendNext(object obj)
        {
            if (!IsWriting)
            {
                throw new InvalidOperationException("PhotonStream is in read mode.");
            }
            writeData.Add(obj);
        }

        public object ReceiveNext()
        {
            if (IsWriting)
            {
                throw new InvalidOperationException("PhotonStream is in write mode.");
            }
            if ((uint)readPosition >= (uint)readData.Length)
            {
                throw new InvalidOperationException("PhotonStream has no more values to read.");
            }
            return readData[readPosition++];
        }

        public object PeekNext()
        {
            if (IsWriting)
            {
                throw new InvalidOperationException("PhotonStream is in write mode.");
            }
            if ((uint)readPosition >= (uint)readData.Length)
            {
                return null;
            }
            return readData[readPosition];
        }

        internal object[] ToArray() => IsWriting ? writeData.ToArray() : readData;
    }

    public readonly struct PhotonMessageInfo
    {
        public PhotonMessageInfo(Player sender, PhotonView photonView)
        {
            Sender = sender;
            this.photonView = photonView;
        }

        public Player Sender { get; }
        public PhotonView photonView { get; }
    }
}
