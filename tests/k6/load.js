// Throughput / latency load test: a pool of accounts under a 50/50 read-write mix, ramping VUs,
// spread across every silo. Thresholds turn it into a pass/fail performance gate.
//   ./tests/run.sh load.js
//   POOL=400 PEAK=400 ./tests/run.sh load.js
import http from 'k6/http';
import { check } from 'k6';
import { BASE, provision, authHeaders, pick } from './lib.js';

// Defaults sized to actually exercise throughput: a wide account pool (POOL) so the write half
// spreads across many single-threaded account actors instead of serializing on a few, plus enough
// VUs (PEAK) to keep them busy. Override per box, e.g. POOL=400 PEAK=400 ./tests/run.sh load.js
const POOL = Number(__ENV.POOL || 200);
const PEAK = Number(__ENV.PEAK || 200);
// Fraction of ops that are writes (deposits). 0 = read-only, 1 = write-only, 0.5 = the default mix.
const WRITE = __ENV.WRITE_RATIO !== undefined ? Number(__ENV.WRITE_RATIO) : 0.5;

export const options = {
  scenarios: {
    ramp: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '10s', target: PEAK }, // ramp up
        { duration: '20s', target: PEAK }, // hold
        { duration: '5s', target: 0 },     // ramp down
      ],
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.02'],            // <2% errors
    http_req_duration: ['p(95)<800'],          // 95% under 800ms
    'http_req_duration{op:read}': ['p(95)<300'],
  },
};

export function setup() {
  const accounts = [];
  for (let i = 0; i < POOL; i++) accounts.push(provision('load', 1000000));
  return { accounts };
}

export default function (data) {
  const acc = pick(data.accounts);
  if (Math.random() >= WRITE) {
    const r = http.get(`${BASE}/accounts/${acc.id}/balance`,
      { headers: authHeaders(acc.token), tags: { op: 'read' } });
    check(r, { 'read 200': x => x.status === 200 });
  } else {
    const r = http.post(`${BASE}/accounts/${acc.id}/deposit`, JSON.stringify({ amount: 1 }),
      { headers: authHeaders(acc.token), tags: { op: 'write' } });
    check(r, { 'write 200': x => x.status === 200 });
  }
}
