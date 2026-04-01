#!/usr/bin/env node
/**
 * MelonMQ – Node.js Producer Test
 *
 * Connects via TCP, authenticates, declares a queue,
 * and publishes 10 messages.
 *
 * Usage:  node producer.js
 * Env:    MELON_HOST (default 127.0.0.1)  MELON_PORT (default 5672)
 */
const { connect } = require('./protocol');
const crypto = require('crypto');

function isSuccess(frame) {
  return frame && frame.type !== 'ERROR' && frame.payload?.success !== false;
}

const MSGS_PER_QUEUE = 5;

// 20 queues with different domains / themes
const QUEUES = [
  { name: 'orders.created',       dlq: 'orders.created.dlq',       ttl: 120000, msgs: (i) => ({ event: 'order_created',    orderId: 1000 + i, total: +(Math.random() * 500).toFixed(2) }) },
  { name: 'orders.paid',          dlq: 'orders.paid.dlq',          ttl: 120000, msgs: (i) => ({ event: 'order_paid',       orderId: 1000 + i, method: ['pix','credit','debit'][i % 3] }) },
  { name: 'orders.shipped',       dlq: 'orders.shipped.dlq',       ttl: 120000, msgs: (i) => ({ event: 'order_shipped',    orderId: 1000 + i, carrier: ['correios','fedex','dhl'][i % 3] }) },
  { name: 'orders.delivered',     dlq: 'orders.delivered.dlq',     ttl: 120000, msgs: (i) => ({ event: 'order_delivered',  orderId: 1000 + i, deliveredAt: new Date().toISOString() }) },
  { name: 'orders.cancelled',     dlq: 'orders.cancelled.dlq',     ttl: 120000, msgs: (i) => ({ event: 'order_cancelled',  orderId: 1000 + i, reason: ['user_request','fraud','timeout'][i % 3] }) },
  { name: 'users.signup',         dlq: 'users.signup.dlq',         ttl: 60000,  msgs: (i) => ({ event: 'user_signup',      userId: 5000 + i, email: `user${i}@example.com` }) },
  { name: 'users.login',          dlq: null,                       ttl: 300000, msgs: (i) => ({ event: 'user_login',       userId: 5000 + i, ip: `192.168.1.${i + 10}` }) },
  { name: 'users.profile_update', dlq: 'users.profile_update.dlq', ttl: 60000,  msgs: (i) => ({ event: 'profile_update',  userId: 5000 + i, field: ['name','avatar','bio'][i % 3] }) },
  { name: 'payments.processing',  dlq: 'payments.dlq',             ttl: 90000,  msgs: (i) => ({ event: 'payment_proc',    txId: `TX-${Date.now()}-${i}`, amount: +(Math.random() * 1000).toFixed(2) }) },
  { name: 'payments.completed',   dlq: 'payments.dlq',             ttl: 90000,  msgs: (i) => ({ event: 'payment_done',    txId: `TX-${Date.now()}-${i}`, status: 'completed' }) },
  { name: 'notifications.email',  dlq: 'notifications.email.dlq',  ttl: 60000,  msgs: (i) => ({ to: `user${i}@mail.com`, subject: `Welcome #${i}`, template: 'welcome' }) },
  { name: 'notifications.sms',    dlq: 'notifications.sms.dlq',    ttl: 60000,  msgs: (i) => ({ phone: `+5511999${String(i).padStart(4,'0')}`, text: `Your code is ${1000 + i}` }) },
  { name: 'notifications.push',   dlq: null,                       ttl: 300000, msgs: (i) => ({ deviceToken: `tok-${i}`, title: 'New offer!', body: `Discount ${10 * i}%` }) },
  { name: 'inventory.reserved',   dlq: 'inventory.dlq',            ttl: 60000,  msgs: (i) => ({ sku: `SKU-${2000 + i}`, qty: i + 1, warehouse: ['SP','RJ','MG'][i % 3] }) },
  { name: 'inventory.released',   dlq: 'inventory.dlq',            ttl: 60000,  msgs: (i) => ({ sku: `SKU-${2000 + i}`, qty: i, reason: 'order_cancelled' }) },
  { name: 'logs.application',     dlq: null,                       ttl: 300000, msgs: (i) => ({ level: ['info','warn','error'][i % 3], service: 'api', msg: `Log entry #${i}` }) },
  { name: 'logs.audit',           dlq: 'logs.audit.dlq',           ttl: 300000, msgs: (i) => ({ action: ['create','update','delete'][i % 3], resource: 'order', actor: `admin-${i}` }) },
  { name: 'analytics.pageview',   dlq: null,                       ttl: 300000, msgs: (i) => ({ page: `/products/${i}`, sessionId: `sess-${Date.now()}`, durationMs: 1000 + i * 200 }) },
  { name: 'analytics.click',      dlq: null,                       ttl: 300000, msgs: (i) => ({ element: `btn-buy-${i}`, page: '/checkout', timestamp: Date.now() }) },
  { name: 'chat.messages',        dlq: 'chat.messages.dlq',        ttl: 60000,  msgs: (i) => ({ room: `room-${(i % 3) + 1}`, from: `user-${i}`, text: `Hey! Message #${i}` }) },
];

(async () => {
  console.log('🍈 MelonMQ Node.js Producer — Multi-Queue');
  console.log('==========================================\n');

  const { socket, request } = await connect();
  console.log(`✓ Connected to ${socket.remoteAddress}:${socket.remotePort}\n`);

  // 1. Auth
  const auth = await request('AUTH', { username: 'guest', password: 'guest' });
  console.log(`Auth → ${auth.type}\n`);

  // 2. Declare all queues
  console.log(`Declaring ${QUEUES.length} queues...\n`);
  for (const q of QUEUES) {
    const decl = await request('DECLAREQUEUE', {
      queue: q.name,
      durable: true,
      deadLetterQueue: q.dlq,
      defaultTtlMs: q.ttl,
    });
    const icon = isSuccess(decl) ? '✓' : '✗';
    console.log(`  ${icon} ${q.name}`);
  }

  // 3. Publish messages to each queue
  const totalMsgs = QUEUES.length * MSGS_PER_QUEUE;
  let sent = 0;
  console.log(`\nPublishing ${MSGS_PER_QUEUE} messages × ${QUEUES.length} queues = ${totalMsgs} total\n`);

  for (const q of QUEUES) {
    for (let i = 0; i < MSGS_PER_QUEUE; i++) {
      const payload = q.msgs(i);
      const bodyBase64 = Buffer.from(JSON.stringify(payload), 'utf-8').toString('base64');

      const res = await request('PUBLISH', {
        queue: q.name,
        bodyBase64,
        persistent: true,
        ttlMs: q.ttl,
        messageId: crypto.randomUUID(),
      });

      sent++;
      const status = isSuccess(res) ? '✓' : '✗';
      console.log(`  ${status} [${sent}/${totalMsgs}] ${q.name} → ${JSON.stringify(payload)}`);
    }
  }

  console.log(`\n✓ Done! ${sent} messages published across ${QUEUES.length} queues.`);
  socket.destroy();
  process.exit(0);
})().catch((err) => {
  console.error('✗ Error:', err.message);
  process.exit(1);
});
