using FishNet;
using FishNet.Object;
using Leguar.TotalJSON;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerDataUpdateService : NetworkBehaviour
{
    private PlayfabRestApiService _playfabService;

    [SerializeField] private string KeyMatchId = "PLAYFAB_MATCH_ID";
    [SerializeField] private string KeyTitleSecret = "PLAYFAB_TITLE_SECRET";
    [SerializeField] private string KeyPlayerIds = "PLAYFAB_MATCH_PLAYERS_ID_LIST";
    [SerializeField] private string ServerPortName = "Game Port";

    private string matchID;
    private string titleSecretKey;
    private string[] playerIdList;

    public override void OnStartServer()
    {
        _playfabService = FindObjectOfType<PlayfabRestApiService>();

        matchID = Environment.GetEnvironmentVariable(KeyMatchId);
        titleSecretKey = Environment.GetEnvironmentVariable(KeyTitleSecret);
        playerIdList = Environment.GetEnvironmentVariable(KeyPlayerIds)?.Split(',');

        string address = Environment.GetEnvironmentVariable("ARBITRIUM_PUBLIC_IP");
        string portsMapping = Environment.GetEnvironmentVariable("ARBITRIUM_PORTS_MAPPING");

        if (!string.IsNullOrEmpty(portsMapping))
        {
            int port = JSON.ParseString(portsMapping).GetJSON("ports").GetJSON(ServerPortName).GetInt("external");

            foreach (string playerId in playerIdList)
            {
                if (!string.IsNullOrEmpty(playerId))
                {
                    _playfabService.UpdatePlayerData(titleSecretKey, playerId, matchID, $"{address}:{port}");
                }
            }
        }
        else
        {
            Debug.Log("Port Mapping Unavailable");
        }
    }

    public override void OnStartClient()
    {
        _playfabService = FindObjectOfType<PlayfabRestApiService>();
        _playfabService.RemovePlayerData();
    }
}
