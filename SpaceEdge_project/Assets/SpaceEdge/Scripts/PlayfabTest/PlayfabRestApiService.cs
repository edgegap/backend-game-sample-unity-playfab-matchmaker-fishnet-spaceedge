using FishNet;
using Leguar.TotalJSON;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public class PlayfabRestApiService : MonoBehaviour
{
    [SerializeField] private string PlayerCustomID = "";
    [SerializeField] private string PlayerTitleID = "";
    [SerializeField] private string TitleID = "";
    [SerializeField] private string QueueName = "";

    private const string TypeHeaderValue = "application/json";
    private readonly HttpClient _playfabHttpClient = new();
    private HttpResponseMessage _request;
    private StringContent _requestData;
    private string _response;
    private string _ticketID = "";
    private string _matchID = "";

    public Action<string, bool> OnStatusUpdate;
    public Action<bool> OnUpdateButton;

    private static PlayfabRestApiService _instance = null;

    private void Awake()
    {
        _playfabHttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(TypeHeaderValue));

        if (_instance is null)
        {
            DontDestroyOnLoad(this);
            _instance = this;
        }
        else
        {
            Destroy(this);
        }
    }

    public async void LoginPlayer()
    {
        var requestJson = new JSON();
        requestJson.Add("CustomID", PlayerCustomID);
        requestJson.Add("CreateAccount", false);
        requestJson.Add("TitleID", TitleID);
        _requestData = new StringContent(requestJson.CreateString(), Encoding.UTF8, TypeHeaderValue);

        _request = await _playfabHttpClient.PostAsync($"https://{TitleID}.playfabapi.com/Client/LoginWithCustomID", _requestData);
        _response = await _request.Content.ReadAsStringAsync();

        if (_request.IsSuccessStatusCode)
        {
            var responseJson = JSON.ParseString(_response);

            string token = responseJson.GetJSON("data").GetJSON("EntityToken").GetString("EntityToken");
            _playfabHttpClient.DefaultRequestHeaders.Add("X-EntityToken", token);

            string sessionTicket = responseJson.GetJSON("data").GetString("SessionTicket");
            _playfabHttpClient.DefaultRequestHeaders.Add("X-Authorization", sessionTicket);

            OnUpdateButton?.Invoke(true);
            OnStatusUpdate?.Invoke("Login Successful", false);
        }
        else
        {
            OnStatusUpdate?.Invoke($"Unable To Login Player, Error {(int)_request.StatusCode} With Message: \n{_response}", true);
        }
    }

    public async void CreateTicket()
    {
        var requestJson = new JSON();

        var entityProperty = new JSON();
        entityProperty.Add("Id", PlayerTitleID);
        entityProperty.Add("Type", "title_player_account");

        var creatorProperty = new JSON();
        creatorProperty.Add("Entity", entityProperty);

        requestJson.Add("Creator", creatorProperty);
        requestJson.Add("GiveUpAfterSeconds", 90);
        requestJson.Add("QueueName", QueueName);
        _requestData = new StringContent(requestJson.CreateString(), Encoding.UTF8, TypeHeaderValue);

        _request = await _playfabHttpClient.PostAsync($"https://{TitleID}.playfabapi.com/Match/CreateMatchmakingTicket", _requestData);
        _response = await _request.Content.ReadAsStringAsync();

        if (_request.IsSuccessStatusCode)
        {
            var responseJson = JSON.ParseString(_response);
            _ticketID = responseJson.GetJSON("data").GetString("TicketId");

            OnStatusUpdate?.Invoke($"Ticket Created, ID #{_ticketID}", false);

            GetMatchIdAttempt();
        }
        else
        {
            OnUpdateButton?.Invoke(true);
            OnStatusUpdate?.Invoke($"Unable To Create Ticket, Error {(int)_request.StatusCode} With Message: \n{_response}", true); 
        }
    }

    private async void GetMatchIdAttempt()
    {
        bool isMatchFound = false;
        var requestJson = new JSON();
        requestJson.Add("EscapeObject", true);
        requestJson.Add("QueueName", QueueName);
        requestJson.Add("TicketId", _ticketID);
        _requestData = new StringContent(requestJson.CreateString(), Encoding.UTF8, TypeHeaderValue);

        while (!isMatchFound)
        {
            _request = await _playfabHttpClient.PostAsync($"https://{TitleID}.playfabapi.com/Match/GetMatchmakingTicket", _requestData);
            _response = await _request.Content.ReadAsStringAsync();

            if (_request.IsSuccessStatusCode)
            {
                var responseJson = JSON.ParseString(_response);
                string ticketStatus = responseJson.GetJSON("data").GetString("Status");
                isMatchFound = ticketStatus == "Matched";

                if (isMatchFound)
                {
                    _matchID = responseJson.GetJSON("data").GetString("MatchId");
                    OnStatusUpdate?.Invoke($"Ticket Has Been Matched, ID #{_matchID}", false);

                    StartConnectionAttempt();
                } 
                else if (ticketStatus == "Canceled")
                {
                    isMatchFound = true;
                    string reason = responseJson.GetJSON("data").GetString("CancellationReason");

                    OnUpdateButton?.Invoke(true);
                    OnStatusUpdate?.Invoke($"Ticket Has Been Cancelled Due To {reason}", false);
                }
                else
                {
                    OnStatusUpdate?.Invoke("Match Not Found, Retrying In 10 Seconds...", false);
                    await Task.Delay(10000);
                }

            }
            else
            {
                isMatchFound = true;
                OnUpdateButton?.Invoke(true);
                OnStatusUpdate?.Invoke($"Could Not Get Ticket Info, Error {(int)_request.StatusCode} With Message: \n{_response}", true);
            }
        }
    }

    private async void StartConnectionAttempt()
    {
        bool gotConnectionInfo = false;

        var requestJson = new JSON();
        requestJson.Add("Keys", new List<string> { _matchID });

        _requestData = new StringContent(requestJson.CreateString(), Encoding.UTF8, TypeHeaderValue);

        while (!gotConnectionInfo)
        {
            _request = await _playfabHttpClient.PostAsync($"https://{TitleID}.playfabapi.com/Client/GetUserData", _requestData);
            _response = await _request.Content.ReadAsStringAsync();

            if (_request.IsSuccessStatusCode)
            {
                var responseJson = JSON.ParseString(_response);
                var dataObject = responseJson.GetJSON("data").GetJSON("Data");

                if (dataObject.Count > 0)
                {
                    gotConnectionInfo = true;
                    string connectionInfo = dataObject.GetJSON(_matchID).GetString("Value");
                    string serverAddress = connectionInfo.Split(':')[0];

                    if (ushort.TryParse(connectionInfo.Split(':')[1], out ushort serverPort))
                    {
                        InstanceFinder.TransportManager.Transport.SetPort(serverPort);
                        InstanceFinder.TransportManager.Transport.SetClientAddress(serverAddress);
                        Debug.Log($"Connecting To Server Using Port {serverPort} And IP {serverAddress}");
                        InstanceFinder.ClientManager.StartConnection();
                    }
                    else
                    {
                        OnUpdateButton?.Invoke(true);
                        OnStatusUpdate?.Invoke($"Error While Parsing Server Port Value", true);
                    }
                }
                else
                {
                    OnStatusUpdate?.Invoke("No Connection Data Found, Retrying In 10 Seconds...", false);
                    await Task.Delay(10000);
                }
            }
            else
            {
                gotConnectionInfo = true;
                OnUpdateButton?.Invoke(true);
                OnStatusUpdate?.Invoke($"Could Not Get Player Data, Error {(int)_request.StatusCode} With Message: \n{_response}", true);
            }
        }
    }

    public async void RemovePlayerData()
    {
        var requestJson = new JSON();
        requestJson.Add("KeysToRemove", new string[] { _matchID });

        _requestData = new StringContent(requestJson.CreateString(), Encoding.UTF8, TypeHeaderValue);
        _request = await _playfabHttpClient.PostAsync($"https://{TitleID}.playfabapi.com/Client/UpdateUserData", _requestData);
        _response = await _request.Content.ReadAsStringAsync();

        if (!_request.IsSuccessStatusCode)
        {
            Debug.Log($"Could Not Update PlayerData, Error {(int)_request.StatusCode} With Message: \n{_response}");
        }
    }

    public async void UpdatePlayerData(string secretKey, string playerID, string key, string value)
    {
        if (!_playfabHttpClient.DefaultRequestHeaders.Contains("X-SecretKey"))
        {
            _playfabHttpClient.DefaultRequestHeaders.Add("X-SecretKey", secretKey);
        }

        var dataProperty = new JSON();
        dataProperty.Add(key, value);

        var requestJson = new JSON();
        requestJson.Add("PlayFabId", playerID);
        requestJson.Add("Data", dataProperty);

        _requestData = new StringContent(requestJson.CreateString(), Encoding.UTF8, TypeHeaderValue);
        _request = await _playfabHttpClient.PostAsync($"https://{TitleID}.playfabapi.com/Server/UpdateUserData", _requestData);
        _response = await _request.Content.ReadAsStringAsync();

        if (!_request.IsSuccessStatusCode)
        {
            Debug.Log($"Could Not Update PlayerData of ID {playerID}, Error {(int)_request.StatusCode} With Message: \n{_response}");
        }
    }
}
