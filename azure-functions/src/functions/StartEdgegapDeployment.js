// Called by the PlayFab rule on "playfab.matchmaking.match_found" (Automation > Rules > Execute Azure Function).
// Looks up the matched players, starts one Edgegap server near them, and returns. Edgegap then calls
// EdgegapDeploymentReady, which hands the server address to the players.
const { app } = require('@azure/functions');
const playfab = require('../lib/playfab');
const edgegap = require('../lib/edgegap');

app.http('StartEdgegapDeployment', {
  methods: ['POST'],
  authLevel: 'function', // the function key is part of the URL registered in PlayFab
  handler: async (request, context) => {
    const body = await request.json().catch(() => ({}));

    // Rule trigger: EntityPlayStreamFunctionExecutionContext. ExecuteFunction (manual test): FunctionArgument.
    const payload = body.PlayStreamEvent?.Payload ?? body.FunctionArgument ?? {};
    const { MatchId: matchId, QueueName: queueName } = payload;
    if (!matchId || !queueName) {
      context.warn('No MatchId/QueueName in the request; is the rule on playfab.matchmaking.match_found?');
      return { status: 400, jsonBody: { error: 'MatchId and QueueName are required.' } };
    }

    // match_found fires for every queue of the title. EDGEGAP_QUEUES (comma-separated) limits which ones get a
    // server here, e.g. while other queues still use PlayFab Multiplayer Servers. Unset = every queue.
    const queues = (process.env.EDGEGAP_QUEUES ?? '').split(',').map((q) => q.trim()).filter(Boolean);
    if (queues.length > 0 && !queues.includes(queueName)) {
      context.log(`Match ${matchId}: queue '${queueName}' is not in EDGEGAP_QUEUES, skipped.`);
      return { jsonBody: { skipped: queueName } };
    }

    try {
      const entityToken = body.TitleAuthenticationContext?.EntityToken ?? (await playfab.getTitleEntityToken());
      const players = await playfab.getMatchPlayers(entityToken, queueName, matchId);

      // Edgegap picks the region closest to all players. PlayFab geolocates each player's last login IP.
      const located = await Promise.all(players.map((p) => playfab.getPlayerLocation(p.playFabId).catch(() => null)));
      let locations = located.filter(Boolean);
      if (locations.length === 0) {
        context.warn(`No player locations for match ${matchId}, using the fallback location.`);
        locations = [
          {
            latitude: Number(process.env.FALLBACK_LATITUDE ?? 45.5019),
            longitude: Number(process.env.FALLBACK_LONGITUDE ?? -73.5674),
          },
        ];
      }

      const deployment = await edgegap.deployForMatch({ matchId, queueName, locations });
      context.log(`Match ${matchId}: Edgegap deployment ${deployment.request_id} for ${players.length} player(s).`);
      return { jsonBody: { requestId: deployment.request_id, players: players.length } };
    } catch (err) {
      context.error(`Match ${matchId}: ${err.message}`);
      return { status: 500, jsonBody: { error: err.message } };
    }
  },
});
