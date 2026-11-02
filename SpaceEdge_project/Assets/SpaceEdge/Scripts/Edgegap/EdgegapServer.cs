using System;
using FishNet;
using FishNet.Managing;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Dedicated server lifecycle on Edgegap. Adds itself at startup in server builds (no scene setup):
/// starts the FishNet server on 7770, and stops its own Edgegap deployment when the match is over,
/// because an empty server still costs money.
/// The server needs no PlayFab secret: the Azure Functions hand the address and a join token to each player,
/// and JoinTokenAuthenticator turns away anyone without one.
/// </summary>
public class EdgegapServer : MonoBehaviour
{
    /// <summary>Internal port. Must equal the Edgegap app version port ("gameport", UDP 7770).</summary>
    public const ushort ListenPort = 7770;

    [Tooltip("Stop the deployment if no matched player connects within this time.")]
    public float FirstPlayerTimeoutSeconds = 120f;

    [Tooltip("Stop the deployment once all players have been gone for this long.")]
    public float EmptyServerTimeoutSeconds = 30f;

    /// <summary>
    /// True in Linux Dedicated Server builds, and in any -batchmode run unless it has -client
    /// (headless clients are used for automated tests).
    /// </summary>
    public static bool IsServer
    {
        get
        {
#if UNITY_SERVER
            return true;
#else
            return Application.isBatchMode && Array.IndexOf(Environment.GetCommandLineArgs(), "-client") < 0;
#endif
        }
    }

    private string _deleteUrl;
    private string _deleteToken;
    private float _startedAt;
    private float _emptySince = -1f;
    private int _lastPlayerCount;
    private bool _anyPlayerJoined;
    private bool _stopping;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (!IsServer)
            return;
        var go = new GameObject(nameof(EdgegapServer));
        DontDestroyOnLoad(go); // survives FishNet's MainMenu -> BattleScene load
        go.AddComponent<EdgegapServer>();
    }

    private void Start()
    {
        Application.targetFrameRate = 60;

        // "Start On Headless" stays off on the ServerManager: this starts the server, on a known port.
        NetworkManager nm = InstanceFinder.NetworkManager;
        JoinTokenAuthenticator.Install(nm); // only the matched players get in
        nm.TransportManager.Transport.SetPort(ListenPort);
        nm.ServerManager.StartConnection();
        Debug.Log($"Edgegap Server | Starting server on 0.0.0.0:{ListenPort}.");

        _deleteUrl = Environment.GetEnvironmentVariable("ARBITRIUM_DELETE_URL");
        _deleteToken = Environment.GetEnvironmentVariable("ARBITRIUM_DELETE_TOKEN");
        _startedAt = Time.realtimeSinceStartup;

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARBITRIUM_REQUEST_ID")))
        {
            Debug.LogWarning("Edgegap Server | Not running on Edgegap, self-stop disabled.");
            enabled = false;
            return;
        }

        Debug.Log(
            $"Edgegap Server | Deployment {Environment.GetEnvironmentVariable("ARBITRIUM_REQUEST_ID")} "
                + $"for PlayFab match {Environment.GetEnvironmentVariable("PLAYFAB_MATCH_ID") ?? "n/a"}."
        );
    }

    private void Update()
    {
        NetworkManager nm = InstanceFinder.NetworkManager;
        if (_stopping || nm == null || !nm.ServerManager.Started)
            return;

        float now = Time.realtimeSinceStartup;
        int players = nm.ServerManager.Clients.Count;
        if (players != _lastPlayerCount)
        {
            Debug.Log($"Edgegap Server | {players} player(s) connected.");
            _lastPlayerCount = players;
        }

        if (players > 0)
        {
            _anyPlayerJoined = true;
            _emptySince = -1f;
            return;
        }

        if (!_anyPlayerJoined)
        {
            if (now - _startedAt > FirstPlayerTimeoutSeconds)
                SelfStop("no player connected in time");
            return;
        }

        if (_emptySince < 0f)
            _emptySince = now;
        else if (now - _emptySince > EmptyServerTimeoutSeconds)
            SelfStop("all players left");
    }

    /// <summary>Stop this Edgegap deployment. Call it from game code when the match ends.</summary>
    public void SelfStop(string reason)
    {
        if (_stopping)
            return;
        _stopping = true;
        Debug.Log($"Edgegap Server | Stopping deployment: {reason}.");

        if (string.IsNullOrEmpty(_deleteUrl) || string.IsNullOrEmpty(_deleteToken))
        {
            Application.Quit();
            return;
        }

        // URL and one-time token are injected by Edgegap into this container only.
        UnityWebRequest request = UnityWebRequest.Delete(_deleteUrl);
        request.SetRequestHeader("authorization", _deleteToken);
        request.SendWebRequest().completed += _ =>
        {
            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("Edgegap Server | Self-stop requested.");
            }
            else
            {
                Debug.LogError($"Edgegap Server | Self-stop failed ({request.responseCode} {request.error}), quitting.");
                Application.Quit();
            }
            request.Dispose();
        };
    }
}
