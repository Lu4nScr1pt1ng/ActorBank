// Functional + auth smoke test: one pass through the whole API, asserting correctness.
// Run: ./k6/run.sh smoke.js
import http from 'k6/http';
import { check } from 'k6';
import { BASE, register, token, openAccount, authHeaders, uniqueUser } from './lib.js';

export const options = {
  vus: 1,
  iterations: 1,
  thresholds: { checks: ['rate==1.0'] }, // every check must pass
};

export default function () {
  const alice = uniqueUser('alice');
  const bob = uniqueUser('bob');

  check(register(alice, 'password123'), { 'register alice -> 201': r => r.status === 201 });
  check(register(bob, 'password123'), { 'register bob -> 201': r => r.status === 201 });

  const ta = token(alice, 'password123');
  const tb = token(bob, 'password123');
  check(ta, { 'alice got a token': t => !!t });

  check(openAccount(alice, ta, 1000), { 'open alice (1000) -> 201': r => r.status === 201 });
  check(openAccount(bob, tb, 0), { 'open bob (0) -> 201': r => r.status === 201 });

  check(http.post(`${BASE}/accounts/${alice}/deposit`, JSON.stringify({ amount: 250 }), { headers: authHeaders(ta) }),
    { 'deposit -> 200': r => r.status === 200 });
  check(http.post(`${BASE}/accounts/${alice}/withdraw`, JSON.stringify({ amount: 75 }), { headers: authHeaders(ta) }),
    { 'withdraw -> 200': r => r.status === 200 });
  check(http.post(`${BASE}/accounts/${alice}/transfer`, JSON.stringify({ toAccountId: bob, amount: 300 }), { headers: authHeaders(ta) }),
    { 'transfer -> 204': r => r.status === 204 });

  check(http.get(`${BASE}/accounts/${alice}/balance`, { headers: authHeaders(ta) }),
    { 'alice balance == 875': r => r.json('balance') === 875 });
  check(http.get(`${BASE}/accounts/${bob}/balance`, { headers: authHeaders(tb) }),
    { 'bob balance == 300': r => r.json('balance') === 300 });

  // --- security ---
  check(http.get(`${BASE}/accounts/${alice}/balance`),
    { 'no token -> 401': r => r.status === 401 });
  check(http.get(`${BASE}/accounts/${alice}/balance`, { headers: authHeaders(tb) }),
    { "bob's token on alice -> 403": r => r.status === 403 });
  check(http.post(`${BASE}/accounts/${alice}/withdraw`, JSON.stringify({ amount: 999999 }), { headers: authHeaders(ta) }),
    { 'overdraw -> 409': r => r.status === 409 });

  // --- ACID: transfer to an unopened account must roll back the debit ---
  check(http.post(`${BASE}/accounts/${alice}/transfer`, JSON.stringify({ toAccountId: uniqueUser('ghost'), amount: 100 }), { headers: authHeaders(ta) }),
    { 'transfer to unopened -> 404': r => r.status === 404 });
  check(http.get(`${BASE}/accounts/${alice}/balance`, { headers: authHeaders(ta) }),
    { 'alice still 875 (debit rolled back)': r => r.json('balance') === 875 });
}
