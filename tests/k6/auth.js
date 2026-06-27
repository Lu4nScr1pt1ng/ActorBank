// Security test under concurrency: every authorization rule must hold on every iteration. If any
// check ever fails, the `checks: rate==1.0` threshold fails the run. Run: ./tests/run.sh auth.js
import http from 'k6/http';
import { check } from 'k6';
import { BASE, register, token, openAccount, authHeaders, uniqueUser } from './lib.js';

const JSON_HEADERS = { 'Content-Type': 'application/json' };

export const options = {
  scenarios: {
    auth: { executor: 'constant-vus', vus: 10, duration: '15s' },
  },
  thresholds: {
    checks: ['rate==1.0'], // a single security violation fails the test
  },
};

export default function () {
  const victim = uniqueUser('victim');
  const attacker = uniqueUser('attacker');
  register(victim, 'password123');
  register(attacker, 'password123');
  const victimToken = token(victim, 'password123');
  const attackerToken = token(attacker, 'password123');

  openAccount(victim, victimToken, 1000);

  check(http.get(`${BASE}/accounts/${victim}/balance`),
    { 'no token -> 401': r => r.status === 401 });
  check(http.get(`${BASE}/accounts/${victim}/balance`, { headers: authHeaders(attackerToken) }),
    { "attacker's token on victim -> 403": r => r.status === 403 });
  check(http.post(`${BASE}/accounts/${victim}/deposit`, JSON.stringify({ amount: 1 }), { headers: authHeaders(attackerToken) }),
    { 'attacker deposit on victim -> 403': r => r.status === 403 });
  check(http.get(`${BASE}/accounts/${victim}/balance`, { headers: { Authorization: `Bearer ${victimToken.slice(0, -2)}XX` } }),
    { 'tampered token -> 401': r => r.status === 401 });
  check(http.post(`${BASE}/auth/token`, JSON.stringify({ username: victim, password: 'wrong' }), { headers: JSON_HEADERS }),
    { 'wrong password -> 401': r => r.status === 401 });
  check(http.post(`${BASE}/auth/register`, JSON.stringify({ username: victim, password: 'password123' }), { headers: JSON_HEADERS }),
    { 'duplicate register -> 409': r => r.status === 409 });

  // The attacks above must not have changed the victim's balance.
  check(http.get(`${BASE}/accounts/${victim}/balance`, { headers: authHeaders(victimToken) }),
    { 'victim balance intact (1000)': r => r.status === 200 && r.json('balance') === 1000 });
}
