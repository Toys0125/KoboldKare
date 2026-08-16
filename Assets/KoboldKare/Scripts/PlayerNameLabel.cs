using Photon.Pun;
using TMPro;
using UnityEngine;

public class PlayerNameLabel : MonoBehaviour {
    private void OnEnable() {
        string displayName = PhotonNetwork.LocalPlayer?.NickName;
        if (string.IsNullOrWhiteSpace(displayName)) {
            displayName = string.IsNullOrWhiteSpace(SettingNickname.CurrentNickname)
                ? SystemInfo.deviceName
                : SettingNickname.CurrentNickname;
        }
        GetComponent<TMP_Text>().text = $"{displayName}:";
    }
}
