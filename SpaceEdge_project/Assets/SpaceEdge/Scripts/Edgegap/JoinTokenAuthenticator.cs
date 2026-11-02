using System;
using System.Collections.Generic;
using FishNet.Authenticating;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

/// <summary>
/// Admits only the players PlayFab matched. StartEdgegapDeployment passes the match's join tokens to the server
/// (EDGEGAP_JOIN_TOKENS), and EdgegapDeploymentReady writes each player's token next to the server address.
/// The client sends its token as soon as it connects; anyone else is kicked. A second connection with the same
/// token replaces the first, so a player whose game crashed can rejoin right away.
/// Added at runtime by EdgegapServer (server) and PlayFabMatchmakingClient (client), so no scene setup.
/// </summary>
public class JoinTokenAuthenticator : Authenticator
{
    public const string TokensVariable = "EDGEGAP_JOIN_TOKENS";

    public override event Action<NetworkConnection, bool> OnAuthenticationResult;

    /// <summary>Client: this player's token for the match, set before connecting.</summary>
    public string Token { get; set; }

    // Server: the match's tokens, and which connection holds each one (one connection per player).
    private HashSet<string> _tokens;
    private readonly Dictionary<string, NetworkConnection> _holders = new Dictionary<string, NetworkConnection>();

    public struct JoinBroadcast : IBroadcast
    {
        public string Token;
    }

    /// <summary>Adds the authenticator to the NetworkManager, or returns the one already there.</summary>
    public static JoinTokenAuthenticator Install(NetworkManager nm)
    {
        var auth = nm.GetComponent<JoinTokenAuthenticator>();
        if (auth == null)
        {
            auth = nm.gameObject.AddComponent<JoinTokenAuthenticator>();
            nm.ServerManager.SetAuthenticator(auth);
        }
        return auth;
    }

    public override void InitializeOnce(NetworkManager networkManager)
    {
        base.InitializeOnce(networkManager);

        string tokens = Environment.GetEnvironmentVariable(TokensVariable);
        if (!string.IsNullOrEmpty(tokens))
            _tokens = new HashSet<string>(tokens.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));

        NetworkManager.ClientManager.OnClientConnectionState += OnClientConnectionState;
        NetworkManager.ServerManager.RegisterBroadcast<JoinBroadcast>(OnJoinBroadcast, false);
        NetworkManager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
    }

    private void OnClientConnectionState(ClientConnectionStateArgs args)
    {
        if (args.ConnectionState == LocalConnectionState.Started)
            NetworkManager.ClientManager.Broadcast(new JoinBroadcast { Token = Token ?? "" });
    }

    private void OnJoinBroadcast(NetworkConnection conn, JoinBroadcast message)
    {
        if (conn.Authenticated)
        {
            conn.Disconnect(true);
            return;
        }

        bool passed;
        if (_tokens == null)
        {
            // Local container or Editor test: no matchmaker, so no tokens to check.
            passed = true;
            Debug.LogWarning($"Edgegap Server | {TokensVariable} not set, client {conn.ClientId} admitted without a token.");
        }
        else
        {
            string token = message.Token ?? "";
            passed = _tokens.Contains(token);
            if (passed)
            {
                // Rejoining (a crashed client's connection lives on until it times out): the newest one wins.
                if (_holders.TryGetValue(token, out NetworkConnection previous) && previous != conn && previous.IsActive)
                {
                    Debug.Log($"Edgegap Server | Client {conn.ClientId} rejoined, dropping its old connection {previous.ClientId}.");
                    previous.Disconnect(true);
                }
                _holders[token] = conn;
            }
            else
            {
                Debug.LogWarning($"Edgegap Server | Client {conn.ClientId} rejected: not a player of this match.");
            }
        }

        OnAuthenticationResult?.Invoke(conn, passed);
    }

    private void OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
    {
        if (args.ConnectionState != RemoteConnectionState.Stopped)
            return;
        foreach (var pair in _holders)
        {
            if (pair.Value == conn)
            {
                _holders.Remove(pair.Key);
                break;
            }
        }
    }
}
