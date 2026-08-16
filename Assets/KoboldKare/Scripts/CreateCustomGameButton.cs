using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;

public class CreateCustomGameButton : MonoBehaviour, IPointerClickHandler {
    [SerializeField] private string serverName = "KoboldKare";
    [SerializeField] private int maxPlayers = 32;
    [SerializeField] private bool privateRoom;

    public void OnPointerClick(PointerEventData eventData) {
        StartCoroutine(CreateCustomGame());
    }

    private IEnumerator CreateCustomGame() {
        yield return NetworkManager.instance.HostBasisMatch(serverName, maxPlayers, privateRoom);
    }
}
