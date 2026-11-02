// Join tokens: the game server only admits the players PlayFab matched.
// Each player's token is derived from the match, the player and the title secret, so StartEdgegapDeployment
// (which gives the server the list) and EdgegapDeploymentReady (which gives each player theirs) agree
// without storing anything. The server only ever sees this match's tokens, never the secret.
const crypto = require('node:crypto');
const { requireSetting } = require('./playfab');

/** Environment variable that carries the match's tokens (comma-separated) to the game server. */
const ENV_KEY = 'EDGEGAP_JOIN_TOKENS';

const joinToken = (matchId, playFabId) =>
  crypto
    .createHmac('sha256', requireSetting('PLAYFAB_SECRET_KEY'))
    .update(`edgegap-join:${matchId}:${playFabId}`)
    .digest('base64url')
    .slice(0, 24);

module.exports = { ENV_KEY, joinToken };
