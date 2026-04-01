#!/usr/bin/env node
/**
 * MelonMQ – Node.js Consumer Test
 *
 * Connects via TCP, authenticates, then fetches ALL existing queues
 * from the HTTP API and subscribes to every one of them.
 * Prints delivered messages (ACKing each one).
 * Stops after 30 s of inactivity or Ctrl+C.
 *
 * Usage:  node consumer.js
 * Env:    MELON_HOST (default 127.0.0.1)  MELON_PORT (default 5672)
 *         MELON_HTTP_PORT (default 9090)
 */
const http = require('http');
const { connect, HOST } = require('./protocol');

function isSuccess(frame) {
  return frame && frame.type !== 'ERROR' && frame.payload?.success !== false;
}

const HTTP_PORT = parseInt(process.env.MELON_HTTP_PORT || '9090', 10);
const PREFETCH = 50;
const IDLE_TIMEOUT_MS = 30_000;

/** Fetch the queue list from the broker's HTTP API */
function fetchQueues() {
  return new Promise((resolve, reject) => {
    http.get(`http://${HOST}:${HTTP_PORT}/stats`, (res) => {
      let data = '';
      res.on('data', (c) => (data += c));
      res.on('end', () => {
        try {
          const stats = JSON.parse(data);
          resolve((stats.queues || []).map((q) => q.name));
        } catch (e) { reject(e); }
      });
    }).on('error', reject);
  });
}

(async () => {
  console.log('🍈 MelonMQ Node.js Consumer — All Queues');
  console.log('=========================================\n');

  // 1. Discover queues via HTTP
  const queues = await fetchQueues();
  if (!queues.length) {
    console.log('No queues found on the broker. Publish some messages first.');
    process.exit(0);
  }
  console.log(`Found ${queues.length} queues: ${queues.join(', ')}\n`);

  // 2. TCP connect + auth
  const { socket, request, send, waitFor } = await connect();
  console.log(`✓ Connected to ${socket.remoteAddress}:${socket.remotePort}\n`);

  const auth = await request('AUTH', { username: 'guest', password: 'guest' });
  console.log(`Auth → ${auth.type}`);

  // 3. Set prefetch
  const pf = await request('SETPREFETCH', { prefetch: PREFETCH });
  console.log(`SetPrefetch(${PREFETCH}) → ${pf.type}\n`);

  // 4. Subscribe to every queue
  console.log(`Subscribing to ${queues.length} queues...\n`);
  for (const q of queues) {
    const sub = await request('CONSUMESUBSCRIBE', { queue: q });
    const icon = isSuccess(sub) ? '✓' : '✗';
    console.log(`  ${icon} ${q}`);
  }

  console.log(`\nWaiting for messages... (Ctrl+C or ${IDLE_TIMEOUT_MS / 1000}s idle to stop)\n`);

  // 5. Consume loop
  let count = 0;
  let idleTimer;

  function resetIdle() {
    clearTimeout(idleTimer);
    idleTimer = setTimeout(() => {
      console.log(`\n⏱  No messages for ${IDLE_TIMEOUT_MS / 1000}s — exiting.`);
      console.log(`Total consumed: ${count} messages`);
      socket.destroy();
      process.exit(0);
    }, IDLE_TIMEOUT_MS);
  }
  resetIdle();

  socket.on('deliver', async (frame) => {
    resetIdle();
    count++;
    const p = frame.payload;
    const body = Buffer.from(p.bodyBase64, 'base64').toString('utf-8');
    console.log(`  📥 [${String(count).padStart(3, '0')}] ${p.queue} → ${body}`);
    console.log(`       tag=${p.deliveryTag}  msgId=${p.messageId}  redelivered=${p.redelivered}`);

    // ACK
    const ackId = send('ACK', { deliveryTag: p.deliveryTag });
    waitFor(ackId, 3000).then((r) => {
      const status = isSuccess(r) ? '✓' : '✗';
      console.log(`       ${status} ACK → ${r.type}`);
    }).catch(() => {});
  });

  // Graceful shutdown
  process.on('SIGINT', () => {
    console.log('\n\nShutting down...');
    clearTimeout(idleTimer);
    socket.destroy();
    console.log(`Total consumed: ${count} messages`);
    process.exit(0);
  });
})().catch((err) => {
  console.error('✗ Error:', err.message);
  process.exit(1);
});
