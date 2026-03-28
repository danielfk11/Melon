/**
 * MelonMQ TCP protocol helper.
 *
 * Wire format: 4-byte LE length prefix + JSON body
 * Frame JSON:  { type: "AUTH" | "DECLAREQUEUE" | ..., corrId: number, payload: object }
 */
const net = require('net');

const HOST = process.env.MELON_HOST || '127.0.0.1';
const PORT = parseInt(process.env.MELON_PORT || '5672', 10);

/** Build a length-prefixed frame buffer */
function buildFrame(type, corrId, payload) {
  const body = JSON.stringify({ type, corrId, payload });
  const jsonBuf = Buffer.from(body, 'utf-8');
  const lenBuf = Buffer.alloc(4);
  lenBuf.writeUInt32LE(jsonBuf.length);
  return Buffer.concat([lenBuf, jsonBuf]);
}

/** Parse frames from an accumulation buffer – returns [frames[], remaining Buffer] */
function parseFrames(buf) {
  const frames = [];
  while (buf.length >= 4) {
    const len = buf.readUInt32LE(0);
    if (buf.length < 4 + len) break;
    const json = buf.subarray(4, 4 + len).toString('utf-8');
    frames.push(JSON.parse(json));
    buf = buf.subarray(4 + len);
  }
  return [frames, buf];
}

/**
 * Open a TCP connection to MelonMQ and return
 * { socket, send(type, payload) → corrId, waitFor(corrId) → frame }
 */
function connect() {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: HOST, port: PORT }, () => {
      let corrSeq = 0;
      let pending = new Map();          // corrId → { resolve, reject, timer }
      let buf = Buffer.alloc(0);

      socket.on('data', (chunk) => {
        buf = Buffer.concat([buf, chunk]);
        const [frames, rest] = parseFrames(buf);
        buf = rest;
        for (const f of frames) {
          const p = pending.get(f.corrId);
          if (p) {
            clearTimeout(p.timer);
            pending.delete(f.corrId);
            p.resolve(f);
          }
          // also emit as generic event for streaming (Deliver)
          if (f.type === 'DELIVER') {
            socket.emit('deliver', f);
          }
        }
      });

      function send(type, payload) {
        const id = ++corrSeq;
        socket.write(buildFrame(type, id, payload));
        return id;
      }

      function waitFor(corrId, timeoutMs = 5000) {
        return new Promise((res, rej) => {
          const timer = setTimeout(() => {
            pending.delete(corrId);
            rej(new Error(`Timeout waiting for corrId ${corrId}`));
          }, timeoutMs);
          pending.set(corrId, { resolve: res, reject: rej, timer });
        });
      }

      async function request(type, payload) {
        const id = send(type, payload);
        return waitFor(id);
      }

      resolve({ socket, send, waitFor, request });
    });

    socket.once('error', reject);
  });
}

module.exports = { connect, buildFrame, parseFrames, HOST, PORT };
