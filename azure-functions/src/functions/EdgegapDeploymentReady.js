// Edgegap webhook (webhook_on_ready / webhook_on_error of the deployment started by StartEdgegapDeployment).
// Re-reads the deployment from the Edgegap API and the players from PlayFab instead of trusting the
// webhook body, then writes the server address into each matched player's read-only data.
const { app } = require('@azure/functions');
const playfab = require('../lib/playfab');
const edgegap = require('../lib/edgegap');
const join = require('../lib/join');

// Name of the port on the Edgegap app version (UDP 7770 for FishNet Tugboat).
const portName = () => process.env.EDGEGAP_PORT_NAME || 'gameport';

app.http('EdgegapDeploymentReady', {
  methods: ['POST'],
  authLevel: 'function',
  handler: async (request, context) => {
    const matchId = request.query.get('matchId');
    const queueName = request.query.get('queue');
    const failed = request.query.get('failed') === 'true';
    const body = await request.json().catch(() => ({}));
    const requestId = body.request_id;

    if (!matchId || !queueName || !requestId) {
      return { status: 400, jsonBody: { error: 'matchId, queue and request_id are required.' } };
    }

    try {
      const deployment = await edgegap.getDeployment(requestId);
      if (!(deployment.tags ?? []).includes(edgegap.matchTag(matchId))) {
        context.warn(`Deployment ${requestId} is not tagged for match ${matchId}, ignoring.`);
        return { status: 403, jsonBody: { error: 'Deployment does not belong to this match.' } };
      }

      let info;
      const port = deployment.ports?.[portName()];
      if (failed || !deployment.running || !port) {
        info = { matchId, status: 'error', message: deployment.current_status ?? 'Server failed to start.' };
        context.error(`Match ${matchId}: deployment ${requestId} failed (${info.message}).`);
      } else {
        info = { matchId, status: 'ready', host: deployment.public_ip, port: port.external, requestId };
      }

      const token = await playfab.getTitleEntityToken();
      const players = await playfab.getMatchPlayers(token, queueName, matchId);
      // Each player gets their own join token; the server admits only these.
      await Promise.all(
        players.map((p) =>
          playfab.setConnectionInfo(
            p.playFabId,
            info.status === 'ready' ? { ...info, joinToken: join.joinToken(matchId, p.playFabId) } : info
          )
        )
      );

      context.log(`Match ${matchId}: sent ${info.status} ${info.host ?? ''}:${info.port ?? ''} to ${players.length} player(s).`);
      return { jsonBody: { status: info.status, players: players.length } };
    } catch (err) {
      context.error(`Match ${matchId}: ${err.message}`);
      return { status: 500, jsonBody: { error: err.message } };
    }
  },
});
