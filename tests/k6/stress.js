// Stress / capacity test: ramp virtual users well past the comfortable load to find where the
// cluster degrades. Aborts early if the error rate crosses 10%, so the summary captures the
// breaking point. Hits the nginx LB, which fans out to the silos. Run:
//   docker compose up -d --scale app=3 && ./tests/run.sh stress.js
import http from 'k6/http';
import { check } from 'k6';
import { BASE, provision, authHeaders, pick } from './lib.js';

const POOL = Number(__ENV.POOL || 40);

export const options = {
  scenarios: {
    stress: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '15s', target: 50 },
        { duration: '15s', target: 100 },
        { duration: '15s', target: 150 },
        { duration: '10s', target: 0 },
      ],
    },
  },
  thresholds: {
    http_req_failed: [{ threshold: 'rate<0.10', abortOnFail: true, delayAbortEval: '10s' }],
  },
};

export function setup() {
  const accounts = [];
  for (let i = 0; i < POOL; i++) accounts.push(provision('stress', 1000000));
  return { accounts };
}

export default function (data) {
  const acc = pick(data.accounts);
  const res = Math.random() < 0.7
    ? http.get(`${BASE}/accounts/${acc.id}/balance`, { headers: authHeaders(acc.token), tags: { op: 'read' } })
    : http.post(`${BASE}/accounts/${acc.id}/deposit`, JSON.stringify({ amount: 1 }), { headers: authHeaders(acc.token), tags: { op: 'write' } });
  check(res, { 'ok': x => x.status === 200 });
}
