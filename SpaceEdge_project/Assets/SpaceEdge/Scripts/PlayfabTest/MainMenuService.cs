using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class MainMenuService : MonoBehaviour
{
    [SerializeField] private Button startGameButton;
    [SerializeField] private TMP_InputField playerNameInput;
    [SerializeField] private TMP_Text serverStatus;

    private PlayfabRestApiService _playfabService;

    private void Start()
    {
        // The dedicated server has no menu: EdgegapServer starts it.
        if (EdgegapServer.IsServer)
            return;

        _playfabService = FindObjectOfType<PlayfabRestApiService>();
        _playfabService.OnStatusUpdate += ServerStatusUpdate;
        _playfabService.OnUpdateButton += UpdateButtonState;

        startGameButton.interactable = false;
        startGameButton.onClick.AddListener(StartGame);
        playerNameInput.onValueChanged.AddListener(NameChanged);
        if (PlayerPrefs.HasKey("PlayerName"))
            playerNameInput.text = PlayerPrefs.GetString("PlayerName");

        _playfabService.LoginPlayer();
    }

    private void OnDestroy()
    {
        if (_playfabService == null)
            return;
        _playfabService.OnStatusUpdate -= ServerStatusUpdate;
        _playfabService.OnUpdateButton -= UpdateButtonState;
    }

    private void StartGame()
    {
        startGameButton.interactable = false;
        serverStatus.text = "";
        _playfabService.CreateTicket();
    }

    private void ServerStatusUpdate(string status, bool isError)
    {
        serverStatus.text += "\n" + status;
        serverStatus.color = isError ? Color.red : Color.green;
        if (isError)
            Debug.LogError(status);
        else
            Debug.Log(status);
    }

    private void UpdateButtonState(bool state) => startGameButton.interactable = state;

    private void NameChanged(string text) => PlayerPrefs.SetString("PlayerName", text);
}
