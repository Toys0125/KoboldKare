using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class CreateRoomOnPress : MonoBehaviour {
    [SerializeField] private TMP_InputField roomNameField;
    [SerializeField] private Slider playerCountSlider;
    [SerializeField] private TMP_Text playerCountLabel;
    [SerializeField] private Toggle privateRoomToggle;
    [SerializeField] private Button button;

    private void Start() {
        playerCountSlider.onValueChanged.AddListener(OnPlayerCountChanged);
        OnPlayerCountChanged(playerCountSlider.value);
        button.onClick.AddListener(OnButtonClicked);
    }

    private void OnButtonClicked() {
        string serverName = string.IsNullOrWhiteSpace(roomNameField.text)
            ? "KoboldKare"
            : roomNameField.text.Trim();
        int maxPlayers = Mathf.RoundToInt(playerCountSlider.value);
        StartCoroutine(CreateRoomRoutine(serverName, maxPlayers, privateRoomToggle.isOn));
        PlayerPrefs.SetString("PrefRoomName", serverName);
    }

    private IEnumerator CreateRoomRoutine(string serverName, int maxPlayers, bool privateRoom) {
        yield return NetworkManager.instance.HostBasisMatch(serverName, maxPlayers, privateRoom);
    }

    private void OnPlayerCountChanged(float value) {
        playerCountLabel.text = Mathf.RoundToInt(value).ToString();
    }
}
