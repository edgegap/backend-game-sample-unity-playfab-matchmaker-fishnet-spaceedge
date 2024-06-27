using FishNet;
using System;
using System.Collections;
using System.Collections.Generic;
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
        if (!InstanceFinder.ServerManager.GetStartOnHeadless())
        {
            _playfabService = FindObjectOfType<PlayfabRestApiService>();
            _playfabService.OnStatusUpdate += ServerStatusUpdate;
            _playfabService.OnUpdateButton += UpdateButtonState;

            startGameButton.onClick.AddListener(StartGame);
            playerNameInput.onValueChanged.AddListener(NameChanged);
            if (PlayerPrefs.HasKey("PlayerName"))
                playerNameInput.text = PlayerPrefs.GetString("PlayerName");

            _playfabService.LoginPlayer();
        }
    }

    private void OnDestroy()
    {
        if (!InstanceFinder.ServerManager.GetStartOnHeadless())
        {
            _playfabService.OnStatusUpdate -= ServerStatusUpdate;
            _playfabService.OnUpdateButton -= UpdateButtonState;
        }
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
        Debug.Log(status);
    }

    private void UpdateButtonState(bool state) 
    { 
        startGameButton.interactable = state;
        serverStatus.text = "";
    }

    private void NameChanged(string text) => PlayerPrefs.SetString("PlayerName", text);
}
