const ws = new WebSocket("ws://127.0.0.1:52100/ws");
let sent = false, pausedSent = false;
ws.onopen = () => {};
ws.onmessage = e => {
  const m = JSON.parse(e.data);
  if (m.type === "init" && !sent) {
    sent = true;
    ws.send(JSON.stringify({ type: "play", id: "dQw4w9WgXcQ", duration: 214, title: "Never Gonna Give You Up", channel: "Rick Astley" }));
    console.log("play sent");
  } else if (m.type === "music-time" || m.type === "music" || m.type === "paused") {
    console.log(m.type + " " + (m.position !== undefined ? "pos=" + m.position.toFixed(1) : "") + (m.playing !== undefined ? " playing=" + m.playing : "") + (m.paused !== undefined ? " paused=" + m.paused : ""));
    if (!pausedSent && m.playing && m.position > 2.5) {
      pausedSent = true;
      ws.send(JSON.stringify({ type: "pause", paused: true }));
      console.log(">>> pause sent");
    }
  }
};
setTimeout(() => { console.log("done"); process.exit(0); }, 25000);