// Shared helpers for the ActorBank k6 scripts.
// One URL: the nginx load balancer (:8080), which fans out across the silo replicas.
import http from 'k6/http';

export const BASE = __ENV.BASE_URL || 'http://localhost:8080';
const JSON_HEADERS = { 'Content-Type': 'application/json' };

export function authHeaders(token) {
  return { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` };
}

export function register(username, password) {
  return http.post(`${BASE}/auth/register`, JSON.stringify({ username, password }), { headers: JSON_HEADERS });
}

export function token(username, password) {
  const res = http.post(`${BASE}/auth/token`, JSON.stringify({ username, password }), { headers: JSON_HEADERS });
  return res.status === 200 ? res.json('accessToken') : null;
}

export function openAccount(id, tok, openingDeposit) {
  return http.post(`${BASE}/accounts/${id}/open`,
    JSON.stringify({ owner: id, openingDeposit }), { headers: authHeaders(tok) });
}

export function uniqueUser(prefix) {
  return `${prefix}_${Date.now().toString(36)}_${Math.floor(Math.random() * 1e9).toString(36)}`;
}

// Registers + logs in + opens an account; returns { id, token }.
export function provision(prefix, openingDeposit) {
  const id = uniqueUser(prefix);
  register(id, 'password123');
  const tok = token(id, 'password123');
  const res = openAccount(id, tok, openingDeposit);
  if (res.status !== 201) throw new Error(`open ${id} failed: ${res.status} ${res.body}`);
  return { id, token: tok };
}

export function pick(arr) {
  return arr[Math.floor(Math.random() * arr.length)];
}
