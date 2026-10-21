using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class MainMenuService : MonoBehaviour
{
    [SerializeField] private Button startGameButton;
    [SerializeField] private TMP_InputField playerNameInput;
    [SerializeField] private TMP_Text serverStatus;

    private PlayFabMatchmakingClient _matchmaking;

    private void Start()
    {
        // The dedicated server has no menu: EdgegapServer starts it.
        if (EdgegapServer.IsServer)
            return;

        _matchmaking = FindObjectOfType<PlayFabMatchmakingClient>();
        _matchmaking.OnStatus += ServerStatusUpdate;
        _matchmaking.OnCanQueue += UpdateButtonState;

        startGameButton.interactable = _matchmaking.IsLoggedIn;
        startGameButton.onClick.AddListener(StartGame);
        playerNameInput.onValueChanged.AddListener(NameChanged);
        if (PlayerPrefs.HasKey("PlayerName"))
            playerNameInput.text = PlayerPrefs.GetString("PlayerName");

        // Back from a match: the client is still logged in.
        if (!_matchmaking.IsLoggedIn)
            _matchmaking.Login();
    }

    private void OnDestroy()
    {
        if (_matchmaking == null)
            return;
        _matchmaking.OnStatus -= ServerStatusUpdate;
        _matchmaking.OnCanQueue -= UpdateButtonState;
    }

    private void StartGame()
    {
        serverStatus.text = "";
        _matchmaking.FindMatch();
    }

    private void ServerStatusUpdate(string status, bool isError)
    {
        serverStatus.text += "\n" + status;
        serverStatus.color = isError ? Color.red : Color.green;
    }

    private void UpdateButtonState(bool state) => startGameButton.interactable = state;

    private void NameChanged(string text) => PlayerPrefs.SetString("PlayerName", text);
}
