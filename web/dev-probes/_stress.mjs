const URL = "ws://127.0.0.1:52100/ws";
const DUR = 35000;
const log = x => console.log(x);
let sent = 0, replies = 0, opens = 0, drops = 0, warm = 0;
const t0 = Date.now();
const ids = new Array(30).fill(0).map((_, i) => "lt" + i);
const pick = a => a[(Math.random() * a.length) | 0];

function action() {
  const r = Math.random();
  if (r < 0.7) return { type: "queue", id: pick(ids) + "." + Math.floor(Math.random() * 1e6), title: "loadtest " + Math.floor(Math.random() * 9999), channel: "x", duration: 90 + Math.floor(Math.random() * 300) };
  if (r < 0.82) return { type: "skip" };
  if (r < 0.92) return { type: "remove", index: Math.floor(Math.random() * 20) };
  if (r < 0.96) return { type: "prev" };
  return { type: "unblock", id: pick(ids) };
}

function worker() {
  try {
    const ws = new WebSocket(URL);
    ws.onopen = () => { opens++; const iv = setInterval(() => {
        if (Date.now() - t0 > DUR) { clearInterval(iv); try { ws.close(); } catch {} return; }
        ws.send(JSON.stringify(action())); sent++;
      }, 450);
      const pv = setInterval(() => { try { ws.send(JSON.stringify({ type: "ping", t: Date.now() })); sent++; } catch {} }, 2500);
      ws._iv = iv; ws._pv = pv;
    };
    ws.onmessage = () => replies++;
    ws.onclose = () => { drops++; clearInterval(ws._iv); clearInterval(ws._pv); };
  } catch {}
}
for (let i = 0; i < 40; i++) worker();

new Array(80).fill(0).forEach(() => {
  const ws = new WebSocket(URL);
  const t = setTimeout(() => { try { ws.close(); } catch {} }, 1200 + Math.random() * 1500);
  ws.onopen = () => { warm++; };
  ws.onmessage = ws.onclose = () => {};
});

const mon = setInterval(() => {
  if (Date.now() - t0 > DUR) {
    clearInterval(mon);
    log("---- probe 2 done ----");
    log("opens=" + opens + " replies=" + replies + " sent=" + sent + " reconnects=" + drops + " shortlivedOpened=" + warm);
    log("two-way ratio=" + (replies / Math.max(1, sent)).toFixed(2));
    const ws = new WebSocket(URL);
    const start = Date.now();
    ws.onopen = () => ws.send(JSON.stringify({ type: "ping", t: Date.now() }));
    ws.onmessage = (ev) => { const m = JSON.parse(ev.data); if (m.type === "pong") { log("FINAL PING OK in " + (Date.now() - start) + "ms"); process.exit(0); } };
    setTimeout(() => { log("final ping timeout"); process.exit(1); }, 8000);
  }
}, 1000);
setTimeout(() => { log("outer timeout"); process.exit(1); }, DUR + 20000);
