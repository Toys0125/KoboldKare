using UnityEngine;

/// <summary>
/// Serialized compatibility shell for the old Photon region selector. Basis server directories
/// expose concrete server endpoints rather than Photon Cloud regions, so region selection is no
/// longer part of the connection model. Existing UI events may continue calling these methods.
/// </summary>
public sealed class PhotonRegionHelper : MonoBehaviour {
    public void RefreshRegions() {
        BasisServerBrowserRefresh.RequestRefresh();
    }

    public void Refresh() {
        RefreshRegions();
    }

    public void JoinLobby() {
        RefreshRegions();
    }

    public void SetRegion(int ignoredRegionIndex) {
        RefreshRegions();
    }

    public void SetRegion(string ignoredRegion) {
        RefreshRegions();
    }
}
