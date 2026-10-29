# PlayFab Matchmaking + Edgegap dedicated servers

Players are matched by **PlayFab Matchmaking**, and each match gets its own **Edgegap** dedicated server, placed near the players. Two small **Azure Functions** (PlayFab's "CloudScript using Azure Functions") connect the two. Nothing secret ships in the game client, the game server holds no PlayFab or Edgegap credentials, and only the matched players can join it.

The game is *SpaceEdge*, a small Unity space shooter on FishNet (Tugboat, UDP 7770).

```
 Game client                PlayFab                      Azure Functions                 Edgegap
 ───────────                ───────                      ───────────────                 ───────
 LoginWithCustomID ───────▶
 CreateMatchmakingTicket ─▶ queue "edgegap-fishnet"
                            match found ──── rule ─────▶ StartEdgegapDeployment
                                                          GetMatch, player locations
                                                          POST /v2/deployments ────────▶ starts the server near
                                                                                         the players
                                                         EdgegapDeploymentReady ◀─────── webhook_on_ready
                                                          checks the deployment,
                            player read-only data ◀────── writes host:port and a join
                                                          token per player
 polls GetUserReadOnlyData ◀
 connects (UDP), sends token ───────────────────────────────────────────────────────▶ FishNet server
                                                                                         admits matched players,
                                                                                         stops itself when
                                                                                         the players leave
```

Tested end to end on 2026-10-21 with Unity 2021.3.45f2, FishNet 3.11.14, Node.js 22 on Azure Functions (Flex Consumption), and two clients matched in PlayFab, connected to the same Edgegap server. Join tokens and the build with the Edgegap plugin came later: they were tested on 2026-10-29 with `npm test` and a local server container (matched tokens admitted, wrong or missing tokens rejected, a crashed client rejoining), not yet in a full PlayFab match on Edgegap.

## What's in the repo

| Path | What it is |
| --- | --- |
| `azure-functions/` | The two Azure Functions (Node.js 22, no PlayFab or Edgegap SDK). `npm test` runs their unit tests. |
| `SpaceEdge_project/` | The Unity project. |
| `SpaceEdge_project/Assets/SpaceEdge/Scripts/PlayfabTest/PlayFabMatchmakingClient.cs` | Client flow: login, ticket, wait for match, wait for server, connect. Only `UnityWebRequest` and `JsonUtility` (no SDK, no JSON library, no threads), so it can also run in WebGL; WebGL has not been tested. |
| `SpaceEdge_project/Assets/SpaceEdge/Scripts/Edgegap/JoinTokenAuthenticator.cs` | FishNet authenticator: the client sends its join token on connect, and the server admits only tokens of its match. |
| `SpaceEdge_project/Assets/SpaceEdge/Scripts/Edgegap/EdgegapServer.cs` | Server lifecycle: starts FishNet in server builds and stops the Edgegap deployment when the match is over. Adds itself at runtime, so no scene setup is needed. |
| `SpaceEdge_project/Assets/SpaceEdge/Editor/EdgegapPluginBuild.cs` | Command-line builds: the Edgegap plugin's **Build server** (scripted tests, CI), and the Windows test client. |
| `SpaceEdge_project/.dockerignore` | The plugin builds the image with the Unity project as Docker context: this sends only `Builds/EdgegapServer/` to Docker. |

The server is built, containerized, pushed and registered with the [Edgegap Unity plugin](https://github.com/edgegap/buildtool-unity-plugin) (`com.edgegap.unity-servers-plugin`, pinned to `#3.2.1` in `Packages/manifest.json`, Editor only), so the project has no Dockerfile of its own.

## Prerequisites

- Unity 2021.3 LTS with **Linux Build Support** and **Linux Dedicated Server Build Support**
- Docker Desktop, running
- An [Edgegap](https://app.edgegap.com) account
- A [PlayFab](https://developer.playfab.com) title
- An Azure subscription and the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli)

## 1. Edgegap: build and register the server

Open `SpaceEdge_project` in Unity, then **Tools → Edgegap Server Hosting**:

1. **Sign in with Edgegap** (or paste a token from **Get token**), then **Validate**.
2. **Build server**. The output goes to `Builds/EdgegapServer/`.
3. **Containerize with Docker**: **Image Name** `playfab-fishnet`, **Image Tag** empty (the plugin uses the current UTC time), default Dockerfile.
4. Optional local check: **Optional Docker Run Parameters** `-p 7770:7770/udp`, then **Deploy local container**. Docker Desktop shows the server starting FishNet on 7770. **Terminate** when done.
5. **Upload to Edgegap**: **Application Name** `playfab-fishnet`, then **Upload image and Create app version**. The plugin pushes the image and opens the dashboard's version form with the image and tag filled in. On it:
   - Port: replace the form's default port with name **`gameport`**, port **7770**, protocol **UDP**.
   - Registry credentials: if the form asks for them, use the username and token from the dashboard's **Container Registry** page.
   - 0.5 vCPU / 1 GB is plenty for this sample. Keep the version name the plugin proposes (the image tag, a UTC timestamp such as `20261029-1800`) and save. The Azure Functions need this name (section 3).

Without the Editor (CI, scripted tests), the same build runs from the command line:
```bash
"<Unity>" -batchmode -projectPath SpaceEdge_project -executeMethod EdgegapPluginBuild.BuildServer -logFile build-server.log
```
For later builds, **Rebuild from Source** in the plugin builds, containerizes and pushes in one click, then opens the version form again.

## 2. PlayFab: queue and title settings

1. **Matchmaking queue** (Multiplayer > Matchmaking > New queue): name `edgegap-fishnet`, match size 2-2, **Enable server allocation off** (Edgegap hosts the servers). No rules are needed for "any two players".
2. **Let clients create players** (gear icon > Title settings > API Features): untick **Disable player creation using Client/LoginWithCustomId**. New titles have it ticked.
3. Note the **Title ID**, and create a **Secret Key** (Title settings > Secret Keys) for the Azure Functions.

## 3. Azure Functions

```bash
az login
az group create -n rg-edgegap-playfab -l westus2
az storage account create -n <storageaccount> -g rg-edgegap-playfab -l westus2 --sku Standard_LRS
az functionapp create -g rg-edgegap-playfab -n <function-app> --storage-account <storageaccount> \
  --flexconsumption-location westus2 --runtime node --runtime-version 22

cd azure-functions
npm ci --omit=dev
# zip host.json, package.json, src/ and node_modules/ into bridge.zip, then:
az functionapp deployment source config-zip -g rg-edgegap-playfab -n <function-app> --src bridge.zip
```

PlayFab recommends the US West regions for Azure Functions it calls.

App settings (Function App > Settings > Environment variables, or `az functionapp config appsettings set`):

| Setting | Value |
| --- | --- |
| `PLAYFAB_TITLE_ID` | Your title ID |
| `PLAYFAB_SECRET_KEY` | The title secret key |
| `EDGEGAP_API_TOKEN` | Edgegap API token (dashboard > User Settings > Tokens), without the `token ` prefix |
| `EDGEGAP_APP_NAME` / `EDGEGAP_APP_VERSION` | `playfab-fishnet` / the version name from section 1, for example `20261029-1800` |
| `EDGEGAP_PORT_NAME` | Optional, defaults to `gameport` |
| `EDGEGAP_QUEUES` | Optional, comma-separated. Only matches from these queues get a server (`match_found` fires for every queue of the title). |
| `EDGEGAP_READY_WEBHOOK_URL` | `https://<function-app>.azurewebsites.net/api/EdgegapDeploymentReady?code=<function key>` |
| `FALLBACK_LATITUDE` / `FALLBACK_LONGITUDE` | Optional. Used when PlayFab has no location for any player. |

Get the function keys with `az functionapp function keys list -g rg-edgegap-playfab -n <function-app> --function-name <name>`.

Then, in PlayFab:
1. **Automation > CloudScript > Functions > Register Function**: name `StartEdgegapDeployment`, URL `https://<function-app>.azurewebsites.net/api/StartEdgegapDeployment?code=<function key>`.
2. **Automation > Rules > New Rule**: event type **`playfab.matchmaking.match_found`**, action **Execute Azure Function > StartEdgegapDeployment**.

`EdgegapDeploymentReady` is called by Edgegap, not PlayFab, so it is not registered in PlayFab.

## 4. Unity client

1. Open `SpaceEdge_project`, scene `Assets/SpaceEdge/Scenes/MainMenu`.
2. On the `PlayfabRestApiService` object (component `PlayFabMatchmakingClient`), set **Title Id** and **Queue Name** (`edgegap-fishnet`).
3. Play: the menu logs in, **Start Game** queues, and the client connects once its server is ready.

To run two clients on one machine, build a Windows client and give each copy its own PlayFab player (PlayerPrefs are shared between them). The build uses Mono, so the Windows IL2CPP module isn't needed. Use `BuildClient` rather than Unity's `-buildWindows64Player`: after a server build, the project is left on the Dedicated Server subtarget.

```bash
"<Unity>" -batchmode -projectPath SpaceEdge_project -executeMethod EdgegapPluginBuild.BuildClient -logFile build-client.log
SpaceEdge_project/Builds/Client/win64/Game.exe -playfabCustomId player-a
SpaceEdge_project/Builds/Client/win64/Game.exe -playfabCustomId player-b
```

Also available: `-playfabTitleId <id>`, `-playfabQueue <name>`, and `-autoMatch` (queues right after login). Headless test clients: add `-batchmode -nographics -client`.

To test against the local container from section 1 without PlayFab, skip matchmaking with `-connect 127.0.0.1:7770`. Without `EDGEGAP_JOIN_TOKENS` the server admits everyone; to try the tokens, run the container with `-e EDGEGAP_JOIN_TOKENS=tokenA,tokenB` and start the clients with `-joinToken tokenA`.

## How it works

- **StartEdgegapDeployment** runs on `match_found`. It reads the match with the title entity token PlayFab passes in, resolves each member's location (`Server/GetPlayerProfile`, from the login IP), and calls Edgegap `POST /v2/deployments` with the players' coordinates, a tag with the start of the match ID (its first 20 characters), the match's join tokens in the hidden variable `EDGEGAP_JOIN_TOKENS`, and ready/error webhooks. It returns in about a second and never waits for the server.
- **EdgegapDeploymentReady** is the webhook. It does not trust the webhook body: it reads the deployment from the Edgegap API, checks that it is tagged with that match, re-reads the match members from PlayFab, and writes `{"matchId","status","host","port","joinToken"}` to each player's **read-only** data key `EdgegapServer`. If the server failed, it writes `status: "error"` so clients stop waiting.
- The **client** polls `GetMatchmakingTicket` every 6 s (PlayFab's limit is 10 calls per minute per ticket), then `GetUserReadOnlyData` every 3 s. It ignores data from an earlier match, connects FishNet to `host:port` and sends its join token.
- **Join tokens** are an HMAC of the match ID and the player's PlayFab ID, keyed with the title secret, so both functions compute the same tokens without storing them. The server only receives this match's tokens.
- The **server** (`EdgegapServer.cs`) starts FishNet on 7770 in server builds, with `JoinTokenAuthenticator`: a client without a token of this match is disconnected. A second connection with the same token replaces the first, so a player whose game crashed can rejoin at once. Without `EDGEGAP_JOIN_TOKENS` (local container, Editor) every client is admitted. It stops its deployment through `ARBITRIUM_DELETE_URL` when nobody connects within 120 s, or 30 s after the last player leaves. Call `SelfStop()` from game code when a match ends.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `PlayerCreationDisabled` at login | Untick *Disable player creation using Client/LoginWithCustomId* (section 2). |
| Matched, but "No server address received" | Look at the Function App logs (Application Insights > Logs, or the Monitor tab of each function). Check that the rule exists and that its event type is exactly `playfab.matchmaking.match_found`. |
| `Edgegap POST /v2/deployments failed (401)` | `EDGEGAP_API_TOKEN` is wrong, or still has the `token ` prefix. |
| Deployment error, image pull | The app version has no private registry credentials. |
| Webhook writes nothing | `EDGEGAP_READY_WEBHOOK_URL` must contain the `code=` key of `EdgegapDeploymentReady`. Edgegap does not retry webhooks. |
| Both clients log in as the same player | Run them with different `-playfabCustomId` values. |
| Server log `Client N rejected: not a player of this match` | The client sent no token or another match's token. Check that both functions run the same code and the same `PLAYFAB_SECRET_KEY`. |

## Security notes

- The client holds only the PlayFab title ID. The title secret and the Edgegap token stay in the Function App settings.
- Server addresses are written to **read-only** player data, which clients can read but not change.
- The game server admits only the matched players (join tokens, see How it works). It never sees the title secret.
- The Edgegap Unity plugin's default Dockerfile (3.2.1) runs `env` before the server, so a deployment's logs show its variables, including `EDGEGAP_JOIN_TOKENS` and its self-stop token. The fix is [buildtool-unity-plugin#62](https://github.com/edgegap/buildtool-unity-plugin/pull/62); until it ships, use a custom Dockerfile without `env` if logs are shared.

## Costs

Free tiers cover testing: PlayFab's development mode, Azure Functions Flex Consumption's monthly free grant, and Edgegap's free tier (one deployment at a time). Servers stop themselves when empty, and the app version caps each deployment at 60 minutes.

## Licence

The code written by Edgegap (`azure-functions/`, `SpaceEdge_project/Assets/SpaceEdge`) is MIT licensed. FishNet and the art and sound packs keep their own licences: see [LICENSE](LICENSE).
