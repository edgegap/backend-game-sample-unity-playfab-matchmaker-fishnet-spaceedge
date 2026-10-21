using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using FishNet;
using Leguar.TotalJSON;
using UnityEngine;

/// <summary>
/// Client side of the PlayFab Matchmaking -> Edgegap flow, using the PlayFab REST API directly:
/// 1. LoginWithCustomID (creates the player on first run)
/// 2. CreateMatchmakingTicket in the queue
/// 3. Poll GetMatchmakingTicket until "Matched"
/// 4. Poll the player's read-only data "EdgegapServer" until the Azure Function writes this match's server
/// 5. Connect FishNet to that host:port
/// The client never talks to Edgegap and holds no secret: the Azure Functions start the server.
/// </summary>
public class PlayfabRestApiService : MonoBehaviour
{
    [Tooltip("PlayFab Title ID (Game Manager > Title settings > API Features). Command line: -playfabTitleId <id>")]
    [SerializeField] private string TitleID = "";

    [Tooltip("PlayFab matchmaking queue name. Command line: -playfabQueue <name>")]
    [SerializeField] private string QueueName = "edgegap-fishnet";

    [Tooltip("PlayFab cancels the ticket if no match is found in this time.")]
    [SerializeField] private int GiveUpAfterSeconds = 120;

    [Tooltip("Give up if no server address arrives this long after the match was found.")]
    [SerializeField] private float ServerWaitTimeoutSeconds = 180f;

    // Read-only player data key written by the EdgegapDeploymentReady Azure Function.
    private const string ConnectionKey = "EdgegapServer";
    private const string TypeHeaderValue = "application/json";
    // PlayFab throttles GetMatchmakingTicket to 10 calls per minute per ticket.
    private const int TicketPollMs = 6000;
    private const int DataPollMs = 3000;

    private readonly HttpClient _http = new();
    private string _entityId;
    private string _entityType;
    private string _ticketId;
    private string _matchId;

    public Action<string, bool> OnStatusUpdate;
    public Action<bool> OnUpdateButton;

    private static PlayfabRestApiService _instance;

    private void Awake()
    {
        if (_instance != null)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);

        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(TypeHeaderValue));
        TitleID = GetArg("-playfabTitleId") ?? TitleID;
        QueueName = GetArg("-playfabQueue") ?? QueueName;
    }

    private string BaseUrl => $"https://{TitleID}.playfabapi.com";

    /// <summary>
    /// One PlayFab player per install. Pass -playfabCustomId to run several clients on one machine
    /// (PlayerPrefs are shared between them, and one player can only hold one ticket per queue).
    /// </summary>
    private static string GetCustomId()
    {
        string id = GetArg("-playfabCustomId");
        if (!string.IsNullOrEmpty(id))
            return id;
        if (!PlayerPrefs.HasKey("PlayFabCustomId"))
            PlayerPrefs.SetString("PlayFabCustomId", Guid.NewGuid().ToString("N"));
        return PlayerPrefs.GetString("PlayFabCustomId");
    }

    public async void LoginPlayer()
    {
        if (string.IsNullOrEmpty(TitleID))
        {
            Status("Set the PlayFab Title ID on PlayfabRestApiService.", true);
            return;
        }

        var body = new JSON();
        body.Add("TitleId", TitleID);
        body.Add("CustomId", GetCustomId());
        body.Add("CreateAccount", true);

        JSON data = await Post("/Client/LoginWithCustomID", body);
        if (data == null)
            return;

        // The menu logs in again each time it is reloaded (after a match).
        _http.DefaultRequestHeaders.Remove("X-Authorization");
        _http.DefaultRequestHeaders.Remove("X-EntityToken");
        _http.DefaultRequestHeaders.Add("X-Authorization", data.GetString("SessionTicket"));
        JSON entityToken = data.GetJSON("EntityToken");
        _http.DefaultRequestHeaders.Add("X-EntityToken", entityToken.GetString("EntityToken"));
        _entityId = entityToken.GetJSON("Entity").GetString("Id");
        _entityType = entityToken.GetJSON("Entity").GetString("Type");

        OnUpdateButton?.Invoke(true);
        Status($"Logged in to PlayFab as {data.GetString("PlayFabId")}", false);

        // Automated tests: -autoMatch queues as soon as the player is logged in.
        if (HasArg("-autoMatch"))
            CreateTicket();
    }

    public async void CreateTicket()
    {
        var entity = new JSON();
        entity.Add("Id", _entityId);
        entity.Add("Type", _entityType);
        var creator = new JSON();
        creator.Add("Entity", entity);

        var body = new JSON();
        body.Add("Creator", creator);
        body.Add("GiveUpAfterSeconds", GiveUpAfterSeconds);
        body.Add("QueueName", QueueName);

        JSON data = await Post("/Match/CreateMatchmakingTicket", body);
        if (data == null)
            return;

        _ticketId = data.GetString("TicketId");
        Status($"Searching for players (queue '{QueueName}')...", false);
        await WaitForMatch();
    }

    private async Task WaitForMatch()
    {
        var body = new JSON();
        body.Add("TicketId", _ticketId);
        body.Add("QueueName", QueueName);
        body.Add("EscapeObject", false);

        while (true)
        {
            JSON data = await Post("/Match/GetMatchmakingTicket", body);
            if (data == null)
                return;

            switch (data.GetString("Status"))
            {
                case "Matched":
                    _matchId = data.GetString("MatchId");
                    Status($"Match found ({_matchId}), starting a server on Edgegap...", false);
                    await WaitForServer();
                    return;
                case "Canceled":
                    Status($"Matchmaking canceled: {data.GetString("CancellationReason")}", true);
                    OnUpdateButton?.Invoke(true);
                    return;
                default:
                    await Task.Delay(TicketPollMs);
                    break;
            }
        }
    }

    private async Task WaitForServer()
    {
        var body = new JSON();
        body.Add("Keys", new[] { ConnectionKey });
        float deadline = Time.realtimeSinceStartup + ServerWaitTimeoutSeconds;

        while (Time.realtimeSinceStartup < deadline)
        {
            JSON data = await Post("/Client/GetUserReadOnlyData", body);
            if (data == null)
                return;

            JSON entries = data.GetJSON("Data");
            if (entries.ContainsKey(ConnectionKey))
            {
                // Ignore what a previous match left behind.
                JSON info = JSON.ParseString(entries.GetJSON(ConnectionKey).GetString("Value"));
                if (info.GetString("matchId") == _matchId)
                {
                    if (info.GetString("status") != "ready")
                    {
                        Status($"The server failed to start: {info.GetString("message")}", true);
                        OnUpdateButton?.Invoke(true);
                        return;
                    }

                    Connect(info.GetString("host"), (ushort)info.GetInt("port"));
                    return;
                }
            }

            await Task.Delay(DataPollMs);
        }

        Status("No server address received. Check the Azure Function logs.", true);
        OnUpdateButton?.Invoke(true);
    }

    private void Connect(string host, ushort port)
    {
        Status($"Server ready at {host}:{port}, connecting...", false);
        InstanceFinder.TransportManager.Transport.SetClientAddress(host);
        InstanceFinder.TransportManager.Transport.SetPort(port);
        InstanceFinder.ClientManager.StartConnection();
    }

    /// <summary>POST to PlayFab and return the "data" object, or null after reporting the error.</summary>
    private async Task<JSON> Post(string path, JSON body)
    {
        try
        {
            var content = new StringContent(body.CreateString(), Encoding.UTF8, TypeHeaderValue);
            HttpResponseMessage response = await _http.PostAsync(BaseUrl + path, content);
            string text = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                return JSON.ParseString(text).GetJSON("data");

            Status($"PlayFab {path} failed ({(int)response.StatusCode}): {text}", true);
        }
        catch (Exception e)
        {
            Status($"PlayFab {path} failed: {e.Message}", true);
        }
        OnUpdateButton?.Invoke(true);
        return null;
    }

    private void Status(string message, bool isError)
    {
        if (OnStatusUpdate != null)
            OnStatusUpdate.Invoke(message, isError);
        else if (isError)
            Debug.LogError(message);
        else
            Debug.Log(message);
    }

    private static bool HasArg(string name) => Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;

    private static string GetArg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
