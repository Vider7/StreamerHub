const ws = new WebSocket("ws://127.0.0.1:52100/ws");
let sent = false, pausedSent = false;
ws.onopen = () => {};
ws.onmessage = e => {
  const m = JSON.parse(e.data);
  if (m.type === "init" && !sent) {
    sent = true;
    ws.send(JSON.stringify({ type: "play", id: "dQw4w9WgXcQ", duration: 214, title: "Never Gonna Give You Up", channel: "Rick Astley" }));
    console.log("play sent");
  } else if (m.type === "music-time") {
    console.log("pos=" + m.position.toFixed(1) + " playing=" + m.playing);
    if (!pausedSent && m.playing && m.position > 3) {
      pausedSent = true;
      ws.send(JSON.stringify({ type: "pause", paused: true }));
      console.log("pause sent at " + m.position.toFixed(1));
      setTimeout(() => process.exit(0), 2500);
    }
  }
};
setTimeout(() => { console.log("timeout"); process.exit(0); }, 30000);