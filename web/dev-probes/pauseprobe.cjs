const WebSocket = require('ws');
const ws = new WebSocket('ws://127.0.0.1:52100/ws');
let n=0;
ws.on('open', ()=> ws.send(JSON.stringify({ type:'play', id:'dQw4w9WgXcQ', duration:214, title:'Never Gonna Give You Up', channel:'Rick Astley' })));
ws.on('message', msg=>{ const m=JSON.parse(msg); if(m.type==='init') { ws.send(JSON.stringify({type:'play',id:'dQw4w9WgXcQ',duration:214,title:'Never Gonna Give You Up',channel:'Rick Astley'})); }
 if(m.type==='music-time'){ if(m.position>3){ ws.send(JSON.stringify({type:'pause',paused:true})); console.log('pause sent at',m.position.toFixed(1)); setTimeout(()=>process.exit(0),1800); } } });
setTimeout(()=>{console.log('done');process.exit(0);},30000);
