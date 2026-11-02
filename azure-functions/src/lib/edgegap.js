// Minimal Edgegap API client. The API token stays in the Function App settings, never in the game client.
const { requireSetting } = require('./playfab');
const join = require('./join');

const API = 'https://api.edgegap.com';

// Edgegap tags are short; the first 20 characters of a PlayFab match ID are unique enough to find it.
const matchTag = (matchId) => matchId.slice(0, 20);

async function call(method, path, body) {
  const res = await fetch(`${API}${path}`, {
    method,
    headers: {
      'Content-Type': 'application/json',
      Authorization: `token ${requireSetting('EDGEGAP_API_TOKEN').replace(/^token\s+/i, '')}`,
    },
    body: body ? JSON.stringify(body) : undefined,
  });
  const json = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(`Edgegap ${method} ${path} failed (${res.status}): ${JSON.stringify(json)}`);
  return json;
}

/**
 * Start one server for the match, placed near the players. Edgegap calls the ready/error webhooks
 * once the container is up (or failed), so nothing here waits for the server.
 */
async function deployForMatch({ matchId, queueName, players, locations }) {
  const webhook = new URL(requireSetting('EDGEGAP_READY_WEBHOOK_URL'));
  webhook.searchParams.set('matchId', matchId);
  webhook.searchParams.set('queue', queueName);
  const errorWebhook = new URL(webhook);
  errorWebhook.searchParams.set('failed', 'true');

  return call('POST', '/v2/deployments', {
    application: requireSetting('EDGEGAP_APP_NAME'),
    version: requireSetting('EDGEGAP_APP_VERSION'),
    users: locations.map((l) => ({ user_type: 'geo_coordinates', user_data: l })),
    tags: [matchTag(matchId), 'playfab'],
    environment_variables: [
      { key: 'PLAYFAB_MATCH_ID', value: matchId, is_hidden: false },
      { key: 'PLAYFAB_QUEUE_NAME', value: queueName, is_hidden: false },
      {
        key: join.ENV_KEY,
        value: players.map((p) => join.joinToken(matchId, p.playFabId)).join(','),
        is_hidden: true,
      },
    ],
    webhook_on_ready: { url: webhook.toString() },
    webhook_on_error: { url: errorWebhook.toString() },
  });
}

/** Authoritative deployment state, so a webhook body is never trusted on its own. */
const getDeployment = (requestId) => call('GET', `/v1/status/${encodeURIComponent(requestId)}`);

module.exports = { matchTag, deployForMatch, getDeployment };
