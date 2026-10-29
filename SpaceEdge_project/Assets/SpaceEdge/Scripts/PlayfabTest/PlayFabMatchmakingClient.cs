// PlayFab Matchmaking -> Edgegap, client side. No PlayFab SDK, no JSON library, no secret: UnityWebRequest +
// JsonUtility only, so it also runs in WebGL. Flow:
//   1. LoginWithCustomID (creates the player on first run)
//   2. CreateMatchmakingTicket in the queue
//   3. Poll GetMatchmakingTicket until "Matched"
//   4. Poll GetUserReadOnlyData["EdgegapServer"] until the EdgegapDeploymentReady Azure Function writes this match's server
//   5. Connect the netcode to host:port with this player's join token (the only netcode-specific code, in the
//      NETCODE ADAPTER region)
// Verified end to end with edgegap/backend-game-sample-unity-playfab-matchmaker-fishnet-spaceedge (FishNet 3.11, Tugboat UDP).
using System;
using System.Text;
using System.Threading.Tasks;
using FishNet;
using UnityEngine;
using UnityEngine.Networking;

[DisallowMultipleComponent]
public class PlayFabMatchmakingClient : MonoBehaviour
{
    [Tooltip("PlayFab Title ID (Game Manager > Title settings > API Features). Command line: -playfabTitleId <id>")]
    public string TitleId = "";

    [Tooltip("PlayFab matchmaking queue name. Command line: -playfabQueue <name>")]
    public string QueueName = "edgegap-fishnet";

    [Tooltip("PlayFab cancels the ticket if no match is found in this time.")]
    public int GiveUpAfterSeconds = 120;

    [Tooltip("Give up if no server address arrives this long after the match was found.")]
    public float ServerWaitTimeoutSeconds = 180f;

    /// <summary>Status text for the UI, and whether it is an error.</summary>
    public event Action<string, bool> OnStatus;

    /// <summary>True when the player may (re)queue: after login and after a failure.</summary>
    public event Action<bool> OnCanQueue;

    public bool IsLoggedIn => !string.IsNullOrEmpty(_entityToken);

    // Read-only player data key written by the EdgegapDeploymentReady Azure Function.
    private const string ConnectionKey = "EdgegapServer";
    // PlayFab throttles GetMatchmakingTicket to 10 calls per minute per ticket.
    private const float TicketPollSeconds = 6f;
    private const float DataPollSeconds = 3f;

    private string _sessionTicket;
    private string _entityToken;
    private EntityKey _entity;
    private string _matchId;
    private static bool _directConnectUsed;

    private static PlayFabMatchmakingClient _instance;

    private void Awake()
    {
        if (_instance != null)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);

        TitleId = GetArg("-playfabTitleId") ?? TitleId;
        QueueName = GetArg("-playfabQueue") ?? QueueName;
    }

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

    public async void Login()
    {
        // Testing a local server container: -connect <host:port> skips PlayFab. Once per run: the menu calls
        // Login again when a match ends, and the player should stay in the menu then.
        string direct = GetArg("-connect");
        if (!string.IsNullOrEmpty(direct) && !_directConnectUsed)
        {
            _directConnectUsed = true;
            int colon = direct.LastIndexOf(':');
            Status($"Connecting directly to {direct}...", false);
            ConnectToServer(direct.Substring(0, colon), ushort.Parse(direct.Substring(colon + 1)), GetArg("-joinToken"));
            return;
        }

        if (string.IsNullOrEmpty(TitleId))
        {
            Status("Set the PlayFab Title ID on PlayFabMatchmakingClient.", true);
            return;
        }

        var data = await Post<LoginResult>("/Client/LoginWithCustomID",
            new LoginRequest { TitleId = TitleId, CustomId = GetCustomId(), CreateAccount = true });
        if (data == null)
            return;

        _sessionTicket = data.SessionTicket;
        _entityToken = data.EntityToken.EntityToken;
        _entity = data.EntityToken.Entity;

        OnCanQueue?.Invoke(true);
        Status($"Logged in to PlayFab as {data.PlayFabId}", false);

        // Automated tests: -autoMatch queues as soon as the player is logged in.
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-autoMatch") >= 0)
            FindMatch();
    }

    public async void FindMatch()
    {
        OnCanQueue?.Invoke(false);
        var ticket = await Post<CreateTicketResult>("/Match/CreateMatchmakingTicket", new CreateTicketRequest
        {
            Creator = new MatchmakingPlayer { Entity = _entity },
            GiveUpAfterSeconds = GiveUpAfterSeconds,
            QueueName = QueueName,
        });
        if (ticket == null)
            return;

        Status($"Searching for players (queue '{QueueName}')...", false);
        var poll = new GetTicketRequest { TicketId = ticket.TicketId, QueueName = QueueName };

        while (true)
        {
            await Wait(TicketPollSeconds);
            var state = await Post<TicketResult>("/Match/GetMatchmakingTicket", poll);
            if (state == null)
                return;

            if (state.Status == "Matched")
            {
                _matchId = state.MatchId;
                Status($"Match found ({_matchId}), starting a server on Edgegap...", false);
                await WaitForServer();
                return;
            }
            if (state.Status == "Canceled")
            {
                Fail($"Matchmaking canceled: {state.CancellationReason}");
                return;
            }
        }
    }

    private async Task WaitForServer()
    {
        var request = new ReadOnlyDataRequest { Keys = new[] { ConnectionKey } };
        float deadline = Time.realtimeSinceStartup + ServerWaitTimeoutSeconds;

        while (Time.realtimeSinceStartup < deadline)
        {
            var data = await Post<ReadOnlyDataResult>("/Client/GetUserReadOnlyData", request);
            if (data == null)
                return;

            string json = data.Data?.EdgegapServer?.Value;
            if (!string.IsNullOrEmpty(json))
            {
                var info = JsonUtility.FromJson<ConnectionInfo>(json);
                // Ignore what a previous match left behind.
                if (info.matchId == _matchId)
                {
                    if (info.status != "ready")
                    {
                        Fail($"The server failed to start: {info.message}");
                        return;
                    }
                    Status($"Server ready at {info.host}:{info.port}, connecting...", false);
                    ConnectToServer(info.host, (ushort)info.port, info.joinToken);
                    return;
                }
            }

            await Wait(DataPollSeconds);
        }

        Fail("No server address received. Check the Azure Function logs.");
    }

    /// <summary>POST to PlayFab and return its "data" object, or null after reporting the error.</summary>
    private async Task<T> Post<T>(string path, object body) where T : class
    {
        using var request = new UnityWebRequest($"https://{TitleId}.playfabapi.com{path}", "POST");
        // Raw JSON body: UnityWebRequest.Post(url, string) form-encodes it before Unity 2022.3.
        request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        if (_sessionTicket != null)
            request.SetRequestHeader("X-Authorization", _sessionTicket);
        if (_entityToken != null)
            request.SetRequestHeader("X-EntityToken", _entityToken);

        UnityWebRequestAsyncOperation op = request.SendWebRequest();
        while (!op.isDone)
            await Task.Yield();

        if (request.result == UnityWebRequest.Result.Success)
            return JsonUtility.FromJson<Envelope<T>>(request.downloadHandler.text).data;

        string error = request.error;
        try
        {
            error = JsonUtility.FromJson<ErrorEnvelope>(request.downloadHandler.text).errorMessage ?? error;
        }
        catch (ArgumentException) { }
        Fail($"PlayFab {path} failed ({request.responseCode}): {error}");
        return null;
    }

    // Task.Delay needs threads, which WebGL doesn't have; this waits on the main thread instead.
    private static async Task Wait(float seconds)
    {
        float end = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < end)
            await Task.Yield();
    }

    private void Fail(string message)
    {
        Status(message, true);
        OnCanQueue?.Invoke(true);
    }

    private void Status(string message, bool isError)
    {
        if (isError)
            Debug.LogError(message);
        else
            Debug.Log(message);
        OnStatus?.Invoke(message, isError);
    }

    private static string GetArg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    #region NETCODE ADAPTER
    // FishNet (Tugboat UDP). NGO: set UnityTransport.ConnectionData (Address, Port) then StartClient().
    // Mirror: NetworkManager.singleton.networkAddress + the transport's Port, then StartClient().
    // The join token is sent by JoinTokenAuthenticator right after connecting (NGO: connection payload).
    private void ConnectToServer(string host, ushort port, string joinToken)
    {
        JoinTokenAuthenticator.Install(InstanceFinder.NetworkManager).Token = joinToken;
        InstanceFinder.TransportManager.Transport.SetClientAddress(host);
        InstanceFinder.TransportManager.Transport.SetPort(port);
        InstanceFinder.ClientManager.StartConnection();
    }
    #endregion

    #region PlayFab REST models (JsonUtility: field names match PlayFab's JSON)
#pragma warning disable 0649
    [Serializable] private class Envelope<T> { public int code; public T data; }
    [Serializable] private class ErrorEnvelope { public string errorMessage; }
    [Serializable] private class EntityKey { public string Id; public string Type; }
    [Serializable] private class LoginRequest { public string TitleId; public string CustomId; public bool CreateAccount; }
    [Serializable] private class EntityTokenInfo { public string EntityToken; public EntityKey Entity; }
    [Serializable] private class LoginResult { public string PlayFabId; public string SessionTicket; public EntityTokenInfo EntityToken; }
    [Serializable] private class MatchmakingPlayer { public EntityKey Entity; }
    [Serializable] private class CreateTicketRequest { public MatchmakingPlayer Creator; public int GiveUpAfterSeconds; public string QueueName; }
    [Serializable] private class CreateTicketResult { public string TicketId; }
    [Serializable] private class GetTicketRequest { public string TicketId; public string QueueName; }
    [Serializable] private class TicketResult { public string Status; public string MatchId; public string CancellationReason; }
    [Serializable] private class ReadOnlyDataRequest { public string[] Keys; }
    [Serializable] private class DataValue { public string Value; }
    [Serializable] private class DataMap { public DataValue EdgegapServer; }
    [Serializable] private class ReadOnlyDataResult { public DataMap Data; }
    [Serializable] private class ConnectionInfo { public string matchId; public string status; public string host; public int port; public string message; public string joinToken; }
#pragma warning restore 0649
    #endregion
}
