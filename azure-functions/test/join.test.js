// node --test: join tokens and the deployment request, with fetch mocked (no PlayFab or Edgegap calls).
const test = require('node:test');
const assert = require('node:assert');

process.env.PLAYFAB_SECRET_KEY = 'test-secret';
process.env.EDGEGAP_API_TOKEN = 'token test-token';
process.env.EDGEGAP_APP_NAME = 'playfab-fishnet';
process.env.EDGEGAP_APP_VERSION = '20261029-1800';
process.env.EDGEGAP_READY_WEBHOOK_URL = 'https://example.azurewebsites.net/api/EdgegapDeploymentReady?code=abc';

const join = require('../src/lib/join');
const edgegap = require('../src/lib/edgegap');

test('join tokens are stable per match and player, and differ otherwise', () => {
  const a = join.joinToken('match-1', 'PLAYER_A');
  assert.strictEqual(a, join.joinToken('match-1', 'PLAYER_A'));
  assert.strictEqual(a.length, 24);
  assert.notStrictEqual(a, join.joinToken('match-1', 'PLAYER_B'));
  assert.notStrictEqual(a, join.joinToken('match-2', 'PLAYER_A'));
  assert.doesNotMatch(a, /,/);
});

test('deployForMatch passes the match tokens as a hidden variable', async () => {
  let sent;
  global.fetch = async (url, init) => {
    sent = { url, init, body: JSON.parse(init.body) };
    return { ok: true, json: async () => ({ request_id: 'abc123' }) };
  };
  const players = [{ playFabId: 'PLAYER_A' }, { playFabId: 'PLAYER_B' }];
  await edgegap.deployForMatch({
    matchId: 'match-1-0123456789abcdef',
    queueName: 'edgegap-fishnet',
    players,
    locations: [{ latitude: 45.5, longitude: -73.6 }],
  });

  assert.strictEqual(sent.url, 'https://api.edgegap.com/v2/deployments');
  assert.strictEqual(sent.init.headers.Authorization, 'token test-token');
  const tokens = sent.body.environment_variables.find((v) => v.key === join.ENV_KEY);
  assert.strictEqual(tokens.is_hidden, true);
  assert.deepStrictEqual(
    tokens.value.split(','),
    players.map((p) => join.joinToken('match-1-0123456789abcdef', p.playFabId))
  );
  assert.deepStrictEqual(sent.body.tags, ['match-1-0123456789ab', 'playfab']);
});
