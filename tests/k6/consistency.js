// ACID consistency under load: many virtual users fire concurrent transfers between a pool of
// accounts (spread across every silo), then we assert the TOTAL money is unchanged. If
// atomicity/isolation ever broke (a debit without its credit, a lost update), the sum would drift.
// Conservation == ACID held.
//   ./tests/run.sh consistency.js
//   ACCOUNTS=20 VUS=30 DURATION=40s ./tests/run.sh consistency.js
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';
import { BASE, provision, authHeaders, pick } from './lib.js';

const ACCOUNTS = Number(__ENV.ACCOUNTS || 12);
const INITIAL = Number(__ENV.INITIAL || 1000);

export const options = {
  scenarios: {
    transfers: {
      executor: 'constant-vus',
      vus: Number(__ENV.VUS || 15),
      duration: __ENV.DURATION || '20s',
    },
  },
  // Passes iff money is conserved and the server never returned an *unexpected* 5xx.
  // A 503 is an expected, retryable outcome under contention — not a failure.
  thresholds: {
    conserved: ['count==1'],
    unexpected_5xx: ['count==0'],
  },
};

const conserved = new Counter('conserved');
const unexpected = new Counter('unexpected_5xx');
const retried = new Counter('retried_503');

export function setup() {
  const accounts = [];
  for (let i = 0; i < ACCOUNTS; i++) accounts.push(provision('cons', INITIAL));
  console.log(`provisioned ${accounts.length} accounts`);
  return { accounts, total: ACCOUNTS * INITIAL };
}

export default function (data) {
  const from = pick(data.accounts);
  const to = pick(data.accounts);
  if (from.id === to.id) return;

  const amount = Math.floor(Math.random() * 10) + 1;
  const body = JSON.stringify({ toAccountId: to.id, amount });
  const headers = authHeaders(from.token);

  let res;
  for (let attempt = 0; attempt < 4; attempt++) {
    res = http.post(`${BASE}/accounts/${from.id}/transfer`, body, { headers, tags: { op: 'transfer' } });
    if (res.status !== 503) break;          // transient contention — back off and retry
    retried.add(1);
    sleep(0.05 * (attempt + 1));
  }

  if (res.status >= 500 && res.status !== 503) unexpected.add(1);
  check(res, { 'transfer settled (204/409, transient 503 ok)': r => [204, 409, 503].includes(r.status) });
}

export function teardown(data) {
  let sum = 0;
  for (const acc of data.accounts) {
    const r = http.get(`${BASE}/accounts/${acc.id}/balance`, { headers: authHeaders(acc.token) });
    sum += r.json('balance');
  }
  const ok = sum === data.total;
  if (ok) conserved.add(1);

  console.log('\n=== MONEY CONSERVATION ===');
  console.log(`sum of balances = ${sum} | expected = ${data.total} | ${ok ? 'CONSERVED — ACID held ✅' : 'VIOLATED ❌'}`);
  check(sum, { 'money conserved under load': s => s === data.total });
}
