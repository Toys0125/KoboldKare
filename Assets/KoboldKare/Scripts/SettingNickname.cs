using Basis.Scripts.BasisSdk.Players;
using UnityEngine;
using UnityScriptableSettings;

[CreateAssetMenu(fileName = "Nickname", menuName = "Unity Scriptable Setting/KoboldKare/Nickname", order = 1)]
public class SettingNickname : SettingString {
    public static string CurrentNickname { get; private set; }

    public override void SetValue(string value) {
        CurrentNickname = value;
        if (BasisLocalPlayer.Instance != null && !string.IsNullOrWhiteSpace(value)) {
            BasisLocalPlayer.Instance.DisplayName = value;
            BasisLocalPlayer.Instance.SetSafeDisplayname();
        }
        base.SetValue(value);
    }
}
