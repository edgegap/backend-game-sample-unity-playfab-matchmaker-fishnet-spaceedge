// Minimal PlayFab REST client for the bridge functions (no SDK needed).
// Entity APIs (Match, Profile) use a title entity token; classic Server APIs use the title secret key.

const titleId = () => requireSetting('PLAYFAB_TITLE_ID');
const secretKey = () => requireSetting('PLAYFAB_SECRET_KEY');

// Player read-only data key the game client polls for its server. Clients can read it but not write it.
const CONNECTION_KEY = 'EdgegapServer';

function requireSetting(name) {
  const value = process.env[name];
  if (!value) throw new Error(`App setting ${name} is not set.`);
  return value;
}

async function call(path, body, headers) {
  const res = await fetch(`https://${titleId()}.playfabapi.com${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...headers },
    body: JSON.stringify(body),
  });
  const json = await res.json().catch(() => ({}));
  if (!res.ok || json.code !== 200) {
    throw new Error(`PlayFab ${path} failed (${res.status}): ${json.errorMessage ?? JSON.stringify(json)}`);
  }
  return json.data;
}

/** Title entity token. PlayFab passes one in every function context; this is the fallback for webhooks. */
async function getTitleEntityToken() {
  const data = await call('/Authentication/GetEntityToken', {}, { 'X-SecretKey': secretKey() });
  return data.EntityToken;
}

/** The match's members as { entityId, playFabId }. */
async function getMatchPlayers(entityToken, queueName, matchId) {
  const match = await call(
    '/Match/GetMatch',
    { QueueName: queueName, MatchId: matchId, EscapeObject: false, ReturnMemberAttributes: false },
    { 'X-EntityToken': entityToken }
  );
  const entities = (match.Members ?? []).map((m) => m.Entity);
  if (entities.length === 0) throw new Error(`Match ${matchId} has no members.`);

  // Matchmaking works on title_player_account entities; Server APIs need the master PlayFab ID.
  const profiles = await call('/Profile/GetProfiles', { Entities: entities }, { 'X-EntityToken': entityToken });
  return profiles.Profiles.map((p) => ({
    entityId: p.Entity.Id,
    playFabId: p.Lineage.MasterPlayerAccountId,
  }));
}

/** Last known location of a player (from their login IP), or null. */
async function getPlayerLocation(playFabId) {
  const data = await call(
    '/Server/GetPlayerProfile',
    { PlayFabId: playFabId, ProfileConstraints: { ShowLocations: true } },
    { 'X-SecretKey': secretKey() }
  );
  const loc = data.PlayerProfile?.Locations?.[0];
  return loc && loc.Latitude != null ? { latitude: loc.Latitude, longitude: loc.Longitude } : null;
}

/** Write the connection info (or an error) where the player's client polls for it. */
async function setConnectionInfo(playFabId, info) {
  await call(
    '/Server/UpdateUserReadOnlyData',
    { PlayFabId: playFabId, Data: { [CONNECTION_KEY]: JSON.stringify(info) } },
    { 'X-SecretKey': secretKey() }
  );
}

module.exports = {
  CONNECTION_KEY,
  requireSetting,
  getTitleEntityToken,
  getMatchPlayers,
  getPlayerLocation,
  setConnectionInfo,
};
