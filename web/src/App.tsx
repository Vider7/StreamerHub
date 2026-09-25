import React, { useEffect, useMemo, useRef } from "react";
import { useHub, TrackResultShape, Track, Music, UpdateInfo, AppInfo, AccountInfo } from "./hub";
import { fmt, fmtClock } from "./format";

type StripDrag = { draggable: true; onDragStart: (e: React.DragEvent) => void; onDragEnd: (e: React.DragEvent) => void; title: string };

let dragGhost: HTMLElement | null = null;
let dragKind: "panel" | "queue" | null = null;

function moveLayout(order: string, from: string, to: string): string {
  const cur = order.toUpperCase().split("");
  const f = cur.indexOf(from);
  const t = cur.indexOf(to);
  if (f < 0 || t < 0 || f === t) return order;
  const tmp = cur[f];
  cur[f] = cur[t];
  cur[t] = tmp;
  return cur.join("");
}

export default function App() {
  const { state, send } = useHub();
  const layout = state.layout || "CSM";
  const [over, setOver] = React.useState<string | null>(null);

  const pos = (ch: string) => {
    const i = layout.toUpperCase().indexOf(ch);
    return i < 0 ? 4 : i + 1;
  };

  const strip = (letter: string): StripDrag => ({
    draggable: true,
    title: "drag to reorder panel",
    onDragStart: (e) => {
      dragKind = "panel";
      e.dataTransfer.setData("text/plain", letter);
      e.dataTransfer.effectAllowed = "move";
      if (dragGhost) {
        dragGhost.remove();
        dragGhost = null;
      }
      const src = e.currentTarget as HTMLElement;
      const panel = src.closest(".panel") as HTMLElement | null;
      if (!panel) return;
      const ghost = panel.cloneNode(true) as HTMLElement;
      const panelRect = panel.getBoundingClientRect();
      const grabRect = src.getBoundingClientRect();
      ghost.style.position = "fixed";
      ghost.style.left = "-10000px";
      ghost.style.top = "-10000px";
      ghost.style.width = panelRect.width + "px";
      ghost.style.pointerEvents = "none";
      ghost.style.boxShadow = "0 14px 44px rgba(0,0,0,0.55)";
      document.body.appendChild(ghost);
      dragGhost = ghost;
      e.dataTransfer.setDragImage(ghost, grabRect.left - panelRect.left + grabRect.width / 2, grabRect.height / 2);
    },
    onDragEnd: () => {
      dragKind = null;
      if (dragGhost) {
        dragGhost.remove();
        dragGhost = null;
      }
    },
  });

  const stripC = useMemo(() => strip("C"), []);
  const stripS = useMemo(() => strip("S"), []);
  const stripM = useMemo(() => strip("M"), []);

  const dropProps = (letter: string) => ({
    "data-letter": letter,
    onDragOver: (e: React.DragEvent<HTMLElement>) => {
      if (dragKind && dragKind !== "panel") return;
      e.preventDefault();
      e.dataTransfer.dropEffect = "move";
      if (over !== letter) setOver(letter);
    },
    onDragLeave: () => setOver((o) => (o === letter ? null : o)),
    onDrop: (e: React.DragEvent<HTMLElement>) => {
      if (dragKind && dragKind !== "panel") return;
      e.preventDefault();
      const from = e.dataTransfer.getData("text/plain");
      setOver(null);
      if (!from || from === letter) return;
      send({ type: "layout", order: moveLayout(layout, from, letter) });
    },
  });

  const appName = state.app?.name ?? "StreamerHub";

  const [theme, setTheme] = React.useState<string>(() => localStorage.getItem("sh.theme") ?? "amber");
  const [settingsOpen, setSettingsOpen] = React.useState(false);
  const [wizSkip, setWizSkip] = React.useState(() => localStorage.getItem("sh.setup.skip") === "1");

  React.useEffect(() => {
    document.documentElement.dataset.theme = theme;
  }, [theme]);

  React.useEffect(() => {
    if (state.connected) send({ type: "theme", id: theme });
  }, [state.connected, theme]);

  const pickTheme = (t: string) => {
    setTheme(t);
    localStorage.setItem("sh.theme", t);
  };

  React.useEffect(() => {
    if (!settingsOpen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setSettingsOpen(false);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [settingsOpen]);

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <span className="brand-dot" aria-hidden="true" />
          <span className="brand-name">{appName}</span>
          <span className="brand-sub">live dashboard</span>
        </div>
        <div className="topmeta">
          <ConnChip label="TW" ok={state.conn?.twitch.ok} detail={state.conn?.twitch.detail} />
          <ConnChip label="TT" ok={state.conn?.tiktok.ok} detail={state.conn?.tiktok.detail} />
          <UpdateChip update={state.update} send={send} />
        </div>
      </header>

      <main className="panels">
        <section className={"panel chat" + (over === "C" ? " over" : "")} {...dropProps("C")} style={{ gridColumnStart: pos("C"), order: pos("C") }}>
          <PanelChatM chat={state.chat} activity={state.activity} strip={stripC} send={send} />
        </section>
        <section className={"panel stats" + (over === "S" ? " over" : "")} {...dropProps("S")} style={{ gridColumnStart: pos("S"), order: pos("S") }}>
          <PanelStatsM stats={state.stats} twitchViewers={state.twitchViewers} strip={stripS} />
        </section>
        <section className={"panel music" + (over === "M" ? " over" : "")} {...dropProps("M")} style={{ gridColumnStart: pos("M"), order: pos("M") }}>
          <PanelMusic music={state.music} pos={state.pos} send={send} appCommand={state.app?.command ?? "!sr"} strip={stripM} results={state.results} />
        </section>
      </main>

      <footer className="statusbar">
        <span className={"led " + (state.connected ? "on" : "")} aria-hidden="true" />
        <span className="status-note">{state.notice || (state.connected ? "connected. music plays in the background app." : "connecting...")}</span>
        <div className="status-right">
          <span className="status-side">
            {state.update?.current ? "v" + state.update.current + " \u00B7 " : ""}
            your settings and notes are saved in the app folder
          </span>
          <button
            className="gearbtn"
            aria-label="open settings"
            title="settings - themes, account, logs"
            onClick={() => {
              setSettingsOpen(true);
              send({ type: "logs" });
            }}
          >
            <i className="fa-solid fa-gear" aria-hidden="true" />
          </button>
        </div>
      </footer>

      <Overlay show={!!(state.setup?.required && !wizSkip)} label="first time setup">
        <SetupWizard
          account={state.account}
          app={state.app}
          send={send}
          onSkip={() => {
            localStorage.setItem("sh.setup.skip", "1");
            setWizSkip(true);
          }}
        />
      </Overlay>

      <Overlay show={settingsOpen} label="settings" onBackdrop={() => setSettingsOpen(false)}>
        <SettingsModal
          account={state.account}
          app={state.app}
          logs={state.logs}
          logError={state.logError}
          send={send}
          theme={theme}
          onTheme={pickTheme}
          onClose={() => setSettingsOpen(false)}
        />
      </Overlay>
      <Overlay show={state.applying} label="installing update" role="alert">
        <div className="modal">
          <div className="modal-head"><span className="modal-title">installing update</span></div>
          <div className="modal-body"><p className="hint">hang on, the new version is swapping in and the app will restart. music resumes on its own.</p></div>
        </div>
      </Overlay>
    </div>
  );
}

function ConnChip({ label, ok, detail }: { label: string; ok?: boolean; detail?: string }) {
  return (
    <span className="chip" title={detail ?? ""}>
      <span className={"led " + (ok ? "ok" : ok === false ? "down" : "")} aria-hidden="true" />
      {label}
    </span>
  );
}

function UpdateChip({ update, send }: { update: UpdateInfo; send: (m: Record<string, unknown>) => void }) {
  const st = update?.status;
  const cur = update?.current ? "v" + update.current : "";
  if (!st || st === "not-configured") return null;
  if (st === "available") {
    return (
      <button
        className="chip upd"
        title={"v" + update.latest + " is ready, click to install and the app restarts"}
        onClick={() => send({ type: "update", action: "apply" })}
      >
        <span className="led ok" aria-hidden="true" />
        <span className="mono">update</span>
      </button>
    );
  }
  if (st === "downloading" || st === "checking") {
    return (
      <span className="chip busy" title={st === "checking" ? "checking for updates" : "downloading update, hang on"}>
        <span className="led" aria-hidden="true" />
        <span className="mono">{st === "checking" ? "check" : "download"}</span>
      </span>
    );
  }
  const up = st === "latest";
  return (
    <button
      className="chip upd"
      title={up ? cur + " up to date, click to check again" : "click to check for updates"}
      onClick={() => send({ type: "update", action: "check" })}
    >
      <span className={"led" + (up ? " ok" : st === "error" ? " down" : "")} aria-hidden="true" />
      <span className="mono">{up ? cur + " ok" : (cur ? cur + " check" : "check")}</span>
    </button>
  );
}

const PanelChatM = React.memo(function PanelChat({ chat, activity, strip, send }: { chat: { _k?: number }[]; activity: { _k?: number }[]; strip: StripDrag; send: (m: Record<string, unknown>) => void }) {
  const rows = (chat as any[]).slice();
  rows.reverse();
  const [split, setSplit] = React.useState(() => {
    const n = Number(localStorage.getItem("sh.chatsplit") ?? "54");
    return Number.isFinite(n) ? Math.min(85, Math.max(15, n)) : 54;
  });
  const wrapRef = React.useRef<HTMLDivElement>(null);
  const dragSplit = React.useRef<{ y: number; split: number; total: number } | null>(null);
  const onSplitDown = (e: React.PointerEvent<HTMLDivElement>) => {
    const wrap = wrapRef.current;
    if (!wrap) return;
    const cl = wrap.querySelector(".chatlist") as HTMLElement | null;
    const ac = wrap.querySelector(".actcol") as HTMLElement | null;
    if (!cl || !ac) return;
    const total = cl.offsetHeight + ac.offsetHeight;
    if (total <= 0) return;
    dragSplit.current = { y: e.clientY, split, total };
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
  };
  const onSplitMove = (e: React.PointerEvent<HTMLDivElement>) => {
    const d = dragSplit.current;
    if (!d) return;
    const next = Math.min(85, Math.max(15, d.split + ((e.clientY - d.y) / d.total) * 100));
    setSplit(next);
    localStorage.setItem("sh.chatsplit", String(Math.round(next)));
  };
  const endSplit = () => {
    dragSplit.current = null;
  };
  const [trPop, setTrPop] = React.useState<{ x: number; y: number; text: string } | null>(null);
  const onChatMenu = (e: React.MouseEvent) => {
    const sel = (window.getSelection()?.toString() ?? "").trim();
    if (sel.length === 0) return;
    e.preventDefault();
    setTrPop({ x: e.clientX, y: e.clientY, text: sel.slice(0, 500) });
  };
  return (
    <div className="panel-inner" ref={wrapRef}>
      <div className="strip" {...strip}>
        <span className="grip" aria-hidden="true"><i className="fa-solid fa-grip-vertical" aria-hidden="true" /></span>
        <span className="strip-title">{"chat"}</span>
        <span className="strip-meta">{chat.length} shown</span>
        <ClearChat send={send} />
      </div>
      <div className="chatlist" style={{ flex: `1 1 ${split}%` }} onContextMenu={onChatMenu}>
        {rows.map((e) => (
          <ChatRow key={e._k ?? e.time + e.msg} e={e} />
        ))}
        {rows.length === 0 && <div className="empty">no messages yet</div>}
        {trPop && (
          <TranslatePop
            x={trPop.x}
            y={trPop.y}
            text={trPop.text}
            onClose={() => setTrPop(null)}
          />
        )}
      </div>
      <div className="chatsplit" role="separator" aria-orientation="horizontal" aria-label="resize activity feed" title="drag to resize activity"
        onPointerDown={onSplitDown} onPointerMove={onSplitMove} onPointerUp={endSplit} onPointerCancel={endSplit} />
      <ActivityFeed activity={activity as any[]} send={send} style={{ flex: `1 1 ${100 - split}%` }} />
    </div>
  );
});

function ActivityFeed({ activity, send, style }: { activity: { _k?: number; kind?: string; time: string; text: string; color?: string }[]; send: (m: Record<string, unknown>) => void; style?: React.CSSProperties }) {
  const [showGifts, setShowGifts] = React.useState(() => (localStorage.getItem("sh.gifts") ?? "1") === "1");
  const rows = activity.slice();
  rows.reverse();
  const filtered = showGifts ? rows : rows.filter((r) => r.kind !== "gift");
  return (
    <div className="actcol" style={style}>
      <div className="actbar">
        <span className="strip-title">activity</span>
        <span className="strip-meta">{filtered.length} shown</span>
        <button
          className={"mini togg" + (showGifts ? " on" : "")}
          title="show or hide tiktok gifts in the activity feed"
          onClick={() => {
            const next = !showGifts;
            setShowGifts(next);
            localStorage.setItem("sh.gifts", next ? "1" : "0");
          }}
        >
          Activity {showGifts ? "on" : "off"}
        </button>
        <ClearChat send={send} msg="activity-clear" label="Clear" done="Cleared" />
      </div>
      <div className="actlist">
        {filtered.map((e) => (
          <ActivityRow key={e._k ?? e.time + e.text} e={e} />
        ))}
        {filtered.length === 0 && <div className="empty">no activity yet</div>}
      </div>
    </div>
  );
}

const ActivityRow = React.memo(function ({ e }: { e: { time: string; kind?: string; text: string; color?: string } }) {
  return (
    <div className={"arow" + (e.kind === "gift" ? " gift" : "") + (e.kind === "bot" ? " bot" : "")}>
      <span className="ctime">{e.time}</span>
      <span className="amsg" style={e.color ? { color: e.color } : undefined}>{e.text}</span>
    </div>
  );
});

const HOLD_MS = 800;

function ClearChat({ send, msg = "chat-clear", action, label = "Clear", done = "Cleared" }: { send: (m: Record<string, unknown>) => void; msg?: string; action?: string; label?: string; done?: string }) {
  const holding = React.useRef(false);
  const timer = React.useRef<number | undefined>(undefined);
  const [fill, setFill] = React.useState(false);
  const [flash, setFlash] = React.useState(false);

  const start = () => {
    if (holding.current) return;
    holding.current = true;
    setFill(true);
    timer.current = window.setTimeout(() => {
      holding.current = false;
      timer.current = undefined;
      setFill(false);
      send(action ? { type: msg, action } : { type: msg });
      setFlash(true);
      window.setTimeout(() => setFlash(false), 1400);
    }, HOLD_MS);
  };

  const cancel = () => {
    holding.current = false;
    if (timer.current) {
      window.clearTimeout(timer.current);
      timer.current = undefined;
    }
    setFill(false);
  };

  const target = msg === "activity-clear" ? "the activity log on this dashboard" : msg === "logs" ? "the app log" : "the chat on this dashboard";

  return (
    <button
      className={"clear-btn" + (fill ? " holding" : "") + (flash ? " done" : "")}
      title={`hold to clear ${target}`}
      aria-label="clear, hold the button to confirm"
      onPointerDown={start}
      onPointerUp={cancel}
      onPointerLeave={cancel}
      onPointerCancel={cancel}
      onKeyDown={(e) => {
        if ((e.key === "Enter" || e.key === " ") && !e.repeat) start();
      }}
      onKeyUp={cancel}
      onBlur={cancel}
    >
      <span className="clear-label">{flash ? done : label}</span>
      <span className="holdbar" aria-hidden="true" />
    </button>
  );
}

function TranslatePop({ x, y, text, onClose }: { x: number; y: number; text: string; onClose: () => void }) {
  const [phase, setPhase] = React.useState<"ask" | "loading" | "done" | "error">("ask");
  const [out, setOut] = React.useState("");
  const [src, setSrc] = React.useState("");
  const ref = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => {
    const down = (e: PointerEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) onClose();
    };
    const key = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("pointerdown", down);
    window.addEventListener("keydown", key);
    return () => {
      window.removeEventListener("pointerdown", down);
      window.removeEventListener("keydown", key);
    };
  }, [onClose]);

  const run = () => {
    setPhase("loading");
    fetch("/api/translate?q=" + encodeURIComponent(text))
      .then(async (r) => {
        if (!r.ok) throw new Error("bad");
        const j = await r.json();
        setOut((j.text as string) ?? "");
        setSrc((j.source as string) ?? "");
        setPhase("done");
      })
      .catch(() => setPhase("error"));
  };

  return (
    <div
      ref={ref}
      className="trpop"
      style={{ left: Math.max(8, Math.min(x, window.innerWidth - 330)), top: Math.max(8, Math.min(y, window.innerHeight - 150)) }}
    >
      <div className="trorig">{text.length > 120 ? text.slice(0, 120) + "..." : text}</div>
      {phase === "ask" && (
        <button className="mini accent" onClick={run}>translate to english</button>
      )}
      {phase === "loading" && <span className="trsrc">translating...</span>}
      {phase === "error" && <span className="trsrc">translation failed</span>}
      {phase === "done" && (
        <>
          <div className="trtext">{out}</div>
          <div className="trsrc">{src && src !== "en" ? "from " + src : "already english"}</div>
        </>
      )}
    </div>
  );
}

function ChatRow({ e }: { e: any }) {
  const tag = e.role === "bot" ? "BOT" : e.tag;
  const [imgOk, setImgOk] = React.useState(true);
  React.useEffect(() => { setImgOk(true); }, [e.avatar]);
  const who = (
    <>
      {e.avatar && imgOk ? (
        <img
          className="chatpfp"
          src={e.avatar}
          alt=""
          loading="lazy"
          referrerPolicy="no-referrer"
          onError={() => setImgOk(false)}
        />
      ) : (
        <span className="chatpfp fallback" style={{ background: e.color || undefined }} title={e.user}>
          {(e.user || "?").charAt(0).toUpperCase()}
        </span>
      )}
      <span className="user" style={{ color: e.color }}>
        {(e.isMod || e.isBroad) && <i className="fa-solid fa-shield-halved modmark" aria-hidden="true" />}
        {e.fanclubBadge ? <img className="fanbadge" src={e.fanclubBadge} alt="" title={e.fanclubName ? e.fanclubName + (e.fanclubLevel > 0 ? " lv" + e.fanclubLevel : "") : "fanclub"} onError={(ev) => { (ev.currentTarget as HTMLImageElement).style.display = "none"; }} /> : null}
        {e.user}
      </span>
    </>
  );
  return (
    <div className={"crow " + (e.role === "bot" ? "bot" : "")}>
      <span className="ctime">{e.time}</span>
      {e.role === "bot" ? (
        <>
          <span className="tag bot">{tag}</span>
          <span className="msg" style={e.color ? { color: e.color } : undefined}>{e.msg}</span>
        </>
      ) : (
        <>
          <span className={"tag " + (e.tag === "TW" ? "tw" : "tt")}>{tag}</span>
          {e.profileUrl ? (
            <a className="who" href={e.profileUrl} target="_blank" rel="noreferrer" title={"open " + e.user + " profile"}>
              {who}
            </a>
          ) : who}
          <span className="msg">{e.msg}</span>
        </>
      )}
    </div>
  );
}

function Srow({ label, value }: { label: string; value: string }) {
  return (
    <div className="srow">
      <span className="stat-label">{label}</span>
      <span className="sval mono">{value}</span>
    </div>
  );
}

const PanelStatsM = React.memo(function PanelStats({ stats, twitchViewers, strip }: { stats: any; twitchViewers: number; strip: StripDrag }) {
  return (
    <div className="panel-inner">
      <div className="strip" {...strip}>
        <span className="grip" aria-hidden="true"><i className="fa-solid fa-grip-vertical" aria-hidden="true" /></span>
        <span className="strip-title">{"stats"}</span>
        <span className="strip-meta">{stats ? fmt(stats.total) + " messages" : ""}</span>
      </div>
      <div className="statgrid">
        <div className="statgroup">audience</div>
        <Srow label="live viewers" value={stats ? fmt(stats.viewers) : "-"} />
        <Srow label="peak viewers" value={stats ? fmt(stats.peakViewers) : "-"} />
        <Srow label="twitch viewers" value={twitchViewers < 0 ? "off" : fmt(twitchViewers)} />
        <Srow label="follows" value={stats ? fmt(stats.follows) : "-"} />
        <Srow label="joins" value={stats ? fmt(stats.joins) : "-"} />
        <div className="statgroup">engagement</div>
        <Srow label="total likes" value={stats ? fmt(stats.totalLikes) : "-"} />
        <Srow label="msg / min" value={stats ? fmt(stats.tiktokPerMin + stats.twitchPerMin) : "-"} />
        <Srow label="shares" value={stats ? fmt(stats.shares) : "-"} />
        <div className="statgroup">gifts</div>
        <Srow label="gifts" value={stats ? fmt(stats.gifts) : "-"} />
        <Srow label="gift value" value={stats ? fmt(stats.giftValue) : "-"} />
      </div>
    </div>
  );
});

function PanelMusic({ music, pos, send, appCommand, strip, results }: { music: Music | null; pos: { position: number; playing: boolean }; send: (m: Record<string, unknown>) => void; appCommand: string; strip: StripDrag; results: TrackResultShape[] }) {
  const [eqOpen, setEqOpen] = React.useState(false);
  const [historyOpen, setHistoryOpen] = React.useState(false);
  const [likedOpen, setLikedOpen] = React.useState(false);
  const [blockedOpen, setBlockedOpen] = React.useState(false);
  return (
    <div className="panel-inner">
      <div className="strip" {...strip}>
        <span className="grip" aria-hidden="true"><i className="fa-solid fa-grip-vertical" aria-hidden="true" /></span>
        <span className="strip-title">{"music"}</span>
        <span className="strip-meta">{music ? music.queue.length + " queued" : ""}</span>
        <button
          className={"mini" + (music?.radio ? " accent" : "")}
          title="when the queue runs out, keep playing similar songs"
          aria-pressed={!!music?.radio}
          onClick={() => send({ type: "radio", on: !music?.radio })}
        >
          {"Radio " + (music?.radio ? "On" : "Off")}
        </button>
        <button
          className={"mini" + (historyOpen ? " accent" : "")}
          title="recently played songs"
          aria-pressed={historyOpen}
          onClick={() => setHistoryOpen((v) => !v)}
        >
          history
        </button>
        <button
          className={"mini" + (likedOpen ? " accent" : "")}
          title="songs you liked"
          aria-pressed={likedOpen}
          onClick={() => setLikedOpen((v) => !v)}
        >
          liked
        </button>
        <button
          className={"mini" + (blockedOpen ? " accent" : "")}
          title="blocked songs - they will never play again"
          aria-pressed={blockedOpen}
          onClick={() => setBlockedOpen((v) => !v)}
        >
          blocked
        </button>
        <button
          className={"mini" + (eqOpen ? " accent" : "")}
          title="equalizer"
          aria-pressed={eqOpen}
          onClick={() => setEqOpen((v) => !v)}
        >
          EQ
        </button>
      </div>
      <NowPlaying music={music} pos={pos} send={send} />
      {historyOpen && <HistoryListM history={music?.history ?? []} send={send} onClose={() => setHistoryOpen(false)} />}
      {likedOpen && <LikedListM liked={music?.liked ?? []} send={send} onClose={() => setLikedOpen(false)} />}
      {blockedOpen && <BlockedListM blocked={music?.blocked ?? []} send={send} onClose={() => setBlockedOpen(false)} />}
      {eqOpen && <EqRowM music={music} send={send} onClose={() => setEqOpen(false)} />}
      <div className="section-label">find a track</div>
      <SearchBoxM send={send} results={results} />
      <div className="section-label">up next</div>
      <QueueListM music={music} send={send} />
    </div>
  );
}

function NowPlaying({ music, pos, send }: { music: Music | null; pos: { position: number; playing: boolean }; send: (m: Record<string, unknown>) => void }) {
  const [cur, setCur] = React.useState((music?.now && pos.playing) ? pos.position : 0);
  const [vol, setVol] = React.useState(music?.volume ?? 25);
  const base = React.useRef({ pos: 0, at: 0 });
  const playingRef = React.useRef(false);
  const volTimer = React.useRef<number | undefined>(undefined);
  const dragCur = React.useRef(0);
  const scrubbing = React.useRef(false);
  const prevIdRef = React.useRef<string | null>(null);

  const serverPos = pos.position;
  const playing = pos.playing;
  playingRef.current = playing;

  useEffect(() => {
    if (music && typeof music.volume === "number" && music.volume !== vol) setVol(music.volume);
  }, [music?.volume]);

  useEffect(() => {
    const id = music?.now?.id ?? null;
    if (prevIdRef.current !== id) {
      prevIdRef.current = id;
      base.current = { pos: 0, at: Date.now() };
      setCur(0);
    }
  }, [music?.now?.id]);

  useEffect(() => {
    base.current = { pos: serverPos, at: Date.now() };
    if (!scrubbing.current) setCur(serverPos);
  }, [serverPos]);

  useEffect(() => {
    const t = window.setInterval(() => {
      if (playingRef.current && !scrubbing.current) setCur(base.current.pos + (Date.now() - base.current.at) / 1000);
    }, 250);
    return () => window.clearInterval(t);
  }, []);

  const commitVol = (v: number) => {
    setVol(v);
    if (volTimer.current) window.clearTimeout(volTimer.current);
    volTimer.current = window.setTimeout(() => send({ type: "volume", value: v }), 250);
  };

  const now = music?.now ?? null;
  const dur = now?.duration ?? 0;
  const pct = dur > 0 ? Math.min(100, Math.max(0, (cur / dur) * 100)) : 0;
  const stateText = now ? (playing ? "PLAYING" : "PAUSED") : "STOPPED";
  const liked = !!now && (music?.liked?.some((l) => l.id === now.id) ?? false);

  const likeCurrent = () => {
    if (!now) return;
    send({ type: "like", id: now.id, title: now.title, channel: now.channel, duration: now.duration });
  };

  const scrub = (clientX: number, el: HTMLDivElement) => {
    if (dur <= 0) return;
    const r = el.getBoundingClientRect();
    const frac = Math.min(1, Math.max(0, (clientX - r.left) / r.width));
    dragCur.current = frac * dur;
    setCur(dragCur.current);
  };

  const onSeekDown = (e: React.PointerEvent<HTMLDivElement>) => {
    const el = e.currentTarget;
    el.setPointerCapture(e.pointerId);
    scrubbing.current = true;
    scrub(e.clientX, el);
    const onMove = (ev: PointerEvent) => scrub(ev.clientX, el);
    const onUp = () => {
      scrubbing.current = false;
      el.removeEventListener("pointermove", onMove);
      el.removeEventListener("pointerup", onUp);
      el.removeEventListener("pointercancel", onUp);
      send({ type: "seek", position: dragCur.current });
    };
    el.addEventListener("pointermove", onMove);
    el.addEventListener("pointerup", onUp);
    el.addEventListener("pointercancel", onUp);
  };

  const seekBy = (delta: number) => send({ type: "seek", position: Math.max(0, cur + delta) });

  return (
    <div className="now">
      <TrackThumb id={now?.id ?? ""} className="art" alt="cover art" />
      <div className="now-main">
        <div className="now-head">
          <span className={"state " + (now ? (playing ? "playing" : "paused") : "")}>{stateText}</span>
          <span className="now-by">{now ? "by " + now.by : "nothing playing"}</span>
        </div>
        <div className="now-title" title={now?.title ?? ""}>{now?.title ?? "queue is empty"}</div>
        {now && <div className="now-meta mono">{fmtClock(cur)} / {fmtClock(dur)}</div>}
        <div
          className="progress"
          role="slider"
          aria-label="seek"
          aria-valuemin={0}
          aria-valuemax={Math.round(dur)}
          aria-valuenow={Math.round(cur)}
          tabIndex={0}
          onPointerDown={onSeekDown}
          onKeyDown={(e) => {
            if (e.key === "ArrowLeft") seekBy(-5);
            if (e.key === "ArrowRight") seekBy(5);
          }}
        >
          <div className="progress-fill" style={{ width: pct + "%" }} />
          {now && <div className="progress-handle" style={{ left: pct + "%" }} />}
        </div>
        <div className="transport">
          <IconBtn title="previous song" onClick={() => send({ type: "prev" })}><i className="fa-solid fa-backward-step" aria-hidden="true" /></IconBtn>
          <button className="play" onClick={() => send({ type: "pause", paused: playingRef.current })} aria-label="toggle play pause">
            {now && !playing ? <i className="fa-solid fa-play" aria-hidden="true" /> : <i className="fa-solid fa-pause" aria-hidden="true" />}
          </button>
          <IconBtn title="skip to next" onClick={() => send({ type: "skip" })}><i className="fa-solid fa-forward-step" aria-hidden="true" /></IconBtn>
          {now && (
            <IconBtn className={"like" + (liked ? " liked" : "")} title={liked ? "undo like" : "like this song"} onClick={likeCurrent}>
              <i className={liked ? "fa-solid fa-heart" : "fa-regular fa-heart"} aria-hidden="true" />
            </IconBtn>
          )}
          {now && (
            <IconBtn className="block" title="block this song - it will never play again" onClick={() => send({ type: "block", id: now.id, title: now.title, channel: now.channel, duration: now.duration })}>
              <i className="fa-solid fa-ban" aria-hidden="true" />
            </IconBtn>
          )}
        </div>
        <div className="volrow">
          <span className="vol-label">vol</span>
          <input
            className="slider"
            type="range"
            min={0}
            max={100}
            value={vol}
            onChange={(e) => commitVol(Number(e.target.value))}
            aria-label="volume"
          />
          <span className="vol-num mono">{vol}</span>
        </div>
      </div>
    </div>
  );
}

const EQ_LABELS = ["31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];
const DEFAULT_BANDS = Array(10).fill(0) as number[];
const EQ_PRESETS: { name: string; bands: number[] }[] = [
  { name: "Flat", bands: [0, 0, 0, 0, 0, 0, 0, 0, 0, 0] },
  { name: "Bass+", bands: [7, 6, 4, 1, 0, 0, 0, 0, 1, 0] },
  { name: "Vocal", bands: [-2, -1, 0, 1, 2, 3, 3, 2, 1, -1] },
  { name: "Club", bands: [6, 5, 2, 0, -1, 0, 1, 2, 4, 4] },
];

const EqRowM = React.memo(function EqRow({ music, send, onClose }: { music: Music | null; send: (m: Record<string, unknown>) => void; onClose: () => void }) {
  const [bands, setBands] = React.useState<number[]>(music?.eq?.length === 10 ? music.eq : DEFAULT_BANDS);
  const [loud, setLoud] = React.useState(music?.loudness ?? false);
  const timer = React.useRef<number | undefined>(undefined);

  useEffect(() => {
    if (music && Array.isArray(music.eq) && music.eq.length === 10) setBands(music.eq);
    if (music && typeof music.loudness === "boolean") setLoud(music.loudness);
  }, [music?.eq, music?.loudness]);

  const queue = (msg: Record<string, unknown>) => {
    if (timer.current) window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => send(msg), 250);
  };

  const setBand = (i: number, v: number) => {
    const next = bands.slice();
    next[i] = v;
    setBands(next);
    queue({ type: "eq", bands: next });
  };

  const applyPreset = (p: number[]) => {
    const next = p.slice();
    setBands(next);
    queue({ type: "eq", bands: next });
  };

  const toggleLoud = () => {
    const next = !loud;
    setLoud(next);
    queue({ type: "loudness", on: next });
  };

  const reset = () => applyPreset(EQ_PRESETS[0].bands);

  const activePreset = (p: number[]) => p.every((v, i) => v === bands[i]);

  return (
    <div>
      <ListHead icon="fa-solid fa-sliders" title="equalizer" count={10} onClose={onClose} />
      <div className="eq">
      <div className="eqpresets">
        {EQ_PRESETS.map((p) => (
          <button key={p.name} className={"mini" + (activePreset(p.bands) ? " eqon" : "")} onClick={() => applyPreset(p.bands)}>
            {p.name}
          </button>
        ))}
        <button className="mini" onClick={reset}>Reset</button>
      </div>
      <div className="eqpanel">
        <div className="eqrack">
          {EQ_LABELS.map((lab, i) => (
            <EqBand key={lab} label={lab} value={bands[i]} onChange={(v) => setBand(i, v)} />
          ))}
        </div>
      </div>
      <div className="eqfoot">
        <span className="eqhint">changes apply live and save automatically</span>
        <button
          className={"mini" + (loud ? " eqon" : "")}
          title="auto-level every song (loudness normalization)"
          aria-pressed={loud}
          onClick={toggleLoud}
        >
          AUTO {loud ? "on" : "off"}
        </button>
      </div>
      </div>
    </div>
  );
});

function EqBand({ label, value, onChange }: { label: string; value: number; onChange: (v: number) => void }) {
  const ref = React.useRef<HTMLDivElement>(null);
  const v = Math.max(-12, Math.min(12, value));
  const pct = ((v + 12) / 24) * 100;

  const setFromY = (clientY: number) => {
    const el = ref.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    const frac = 1 - (clientY - r.top) / r.height;
    const next = Math.round((Math.max(0, Math.min(1, frac)) * 24 - 12) * 2) / 2;
    onChange(next);
  };

  return (
    <div className="eqband">
      <span className={"eqval mono" + (v !== 0 ? " on" : "")}>{v > 0 ? "+" : ""}{v.toFixed(1)}</span>
      <div
        className="eq-track"
        ref={ref}
        role="slider"
        aria-label={"eq " + label}
        aria-valuemin={-12}
        aria-valuemax={12}
        aria-valuenow={v}
        tabIndex={0}
        onPointerDown={(e) => {
          e.preventDefault();
          (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
          setFromY(e.clientY);
          const onMove = (ev: PointerEvent) => setFromY(ev.clientY);
          const onUp = () => {
            window.removeEventListener("pointermove", onMove);
            window.removeEventListener("pointerup", onUp);
            window.removeEventListener("pointercancel", onUp);
          };
          window.addEventListener("pointermove", onMove);
          window.addEventListener("pointerup", onUp);
          window.addEventListener("pointercancel", onUp);
        }}
        onKeyDown={(e) => {
          if (e.key === "ArrowUp" || e.key === "ArrowRight") onChange(Math.min(12, v + 0.5));
          if (e.key === "ArrowDown" || e.key === "ArrowLeft") onChange(Math.max(-12, v - 0.5));
        }}
      >
        <span className="eq-fill" style={{ height: pct + "%" }} />
        <span className="eq-handle" style={{ top: 100 - pct + "%" }} />
      </div>
      <span className="eqfreq">{label}</span>
    </div>
  );
}

function TrackThumb({ id, className, alt }: { id: string; className?: string; alt?: string }) {
  const [ok, setOk] = React.useState(true);
  useEffect(() => {
    setOk(true);
  }, [id]);
  if (!id || !ok) {
    return (
      <div className={"noart " + (className ?? "")} aria-hidden="true"><i className="fa-solid fa-music" aria-hidden="true" /></div>
    );
  }
  return (
    <img className={className} src={"/api/thumb/" + encodeURIComponent(id)} alt={alt ?? ""} loading="lazy" onError={() => setOk(false)} />
  );
}

const QueueListM = React.memo(function QueueList({ music, send }: { music: Music | null; send: (m: Record<string, unknown>) => void }) {
  const q = music?.queue ?? [];
  const [from, setFrom] = React.useState<number | null>(null);
  const [over, setOver] = React.useState<number | null>(null);
  const finish = () => { dragKind = null; setFrom(null); setOver(null); };
  return (
    <div className="queuelist" onDragOver={(e) => e.preventDefault()} onDrop={(e) => {
      e.preventDefault();
      const f = from ?? Number(e.dataTransfer.getData("text/plain"));
      if (Number.isFinite(f) && over !== null && f !== over) send({ type: "move", from: f, to: over });
      finish();
    }}>
      {q.map((t, i) => (
        <div className={"qrow" + (over === i ? " dropto" : "")} key={t.id + i}
          draggable
          onDragStart={(e) => { dragKind = "queue"; setFrom(i); e.dataTransfer.setData("text/plain", String(i)); e.dataTransfer.effectAllowed = "move"; }}
          onDragOver={(e) => { if (dragKind && dragKind !== "queue") return; e.preventDefault(); e.dataTransfer.dropEffect = "move"; if (over !== i) setOver(i); }}
          onDragEnd={finish}>
          <TrackThumb id={t.id} className="qart" alt="" />
          <span className="qidx mono">{i + 1}</span>
          <span className="qtitle" title={t.title}>{t.title}</span>
          <span className="qby">{t.by}</span>
          <span className="qdur mono">{t.durationLabel}</span>
          <button className="qx" onClick={() => send({ type: "remove", index: i })} aria-label="remove from queue" title="remove from queue"><i className="fa-solid fa-xmark" aria-hidden="true" /></button>
        </div>
      ))}
      {q.length === 0 && <div className="empty">queue is empty, request songs in chat</div>}
    </div>
  );
});

function playTrack(send: (m: Record<string, unknown>) => void, t: Track) {
  send({ type: "play", id: t.id, title: t.title, channel: t.channel, duration: t.duration });
}

function ListHead({ icon, title, count, onClose }: { icon: string; title: string; count: number; onClose: () => void }) {
  return (
    <div className="listhead">
      <i className={icon} aria-hidden="true" />
      <span className="strip-title">{title}</span>
      <span className="strip-meta">{count}</span>
      <button className="qx" onClick={onClose} aria-label={"close " + title} title="close"><i className="fa-solid fa-xmark" aria-hidden="true" /></button>
    </div>
  );
}

const HistoryListM = React.memo(function HistoryList({ history, send, onClose }: { history: Track[]; send: (m: Record<string, unknown>) => void; onClose: () => void }) {
  return (
    <div>
      <ListHead icon="fa-solid fa-clock-rotate-left" title="history" count={history.length} onClose={onClose} />
      <div className="queuelist">
      {history.map((t) => (
        <div className="qrow" key={t.id}>
          <TrackThumb id={t.id} className="qart" alt="" />
          <span className="qtitle" title={t.title}>{t.title}</span>
          <span className="qby">{t.by}</span>
          <span className="qdur mono">{t.durationLabel}</span>
          <button className="mini" onClick={() => playTrack(send, t)} aria-label="play from history">{"play"}</button>
        </div>
      ))}
      {history.length === 0 && <div className="empty">no songs played yet</div>}
      </div>
    </div>
  );
});

const LikedListM = React.memo(function LikedList({ liked, send, onClose }: { liked: Track[]; send: (m: Record<string, unknown>) => void; onClose: () => void }) {
  return (
    <div>
      <ListHead icon="fa-solid fa-heart" title="liked" count={liked.length} onClose={onClose} />
      <div className="queuelist">
      {liked.map((t) => (
        <div className="qrow" key={t.id}>
          <TrackThumb id={t.id} className="qart" alt="" />
          <span className="qtitle" title={t.title}>{t.title}</span>
          <span className="qby">{t.by}</span>
          <span className="qdur mono">{t.durationLabel}</span>
          <button className="mini" onClick={() => playTrack(send, t)} aria-label="play liked song">{"play"}</button>
          <button className="mini" onClick={() => send({ type: "like", id: t.id, title: t.title, channel: t.channel, duration: t.duration })} aria-label="remove from liked">{"unlike"}</button>
        </div>
      ))}
      {liked.length === 0 && <div className="empty">no liked songs yet</div>}
      </div>
    </div>
  );
});

const BlockedListM = React.memo(function BlockedList({ blocked, send, onClose }: { blocked: Track[]; send: (m: Record<string, unknown>) => void; onClose: () => void }) {
  return (
    <div>
      <ListHead icon="fa-solid fa-ban" title="blocked" count={blocked.length} onClose={onClose} />
      <div className="queuelist">
      {blocked.map((t) => (
        <div className="qrow" key={t.id}>
          <TrackThumb id={t.id} className="qart" alt="" />
          <span className="qtitle" title={t.title}>{t.title}</span>
          <span className="qby">{t.by}</span>
          <span className="qdur mono">{t.durationLabel}</span>
          <button className="qx" onClick={() => send({ type: "unblock", id: t.id })} aria-label="unblock song" title="unblock - it can play again"><i className="fa-solid fa-xmark" aria-hidden="true" /></button>
        </div>
      ))}
      {blocked.length === 0 && <div className="empty">no blocked songs</div>}
      </div>
    </div>
  );
});

const SearchBoxM = React.memo(function SearchBox({ send, results }: { send: (m: Record<string, unknown>) => void; results: TrackResultShape[] }) {
  const [q, setQ] = React.useState("");
  const t = React.useRef<number | undefined>(undefined);
  const lastSent = React.useRef("");
  const settledFor = React.useRef("");

  useEffect(() => {
    settledFor.current = q;
  }, [results]);

  const runSearch = (text: string) => {
    send({ type: "search", q: text });
  };

  const pick = (r: TrackResultShape, kind: "play" | "queue") => {
    send({ type: kind, id: r.id, title: r.title, channel: r.channel, duration: r.duration });
    lastSent.current = "";
    setQ("");
    send({ type: "search", q: "" });
  };

  const searching = q.trim().length > 0 && results.length === 0 && settledFor.current !== q.trim();
  const noResults = q.trim().length > 0 && results.length === 0 && settledFor.current === q.trim();

  return (
    <div>
      <div className="searchrow">
        <input
          className="text"
          value={q}
          placeholder="search youtube..."
          onChange={(e) => {
            const v = e.target.value;
            setQ(v);
            const text = v.trim();
            if (t.current) window.clearTimeout(t.current);
            if (text.length === 0) {
              lastSent.current = "";
              send({ type: "search", q: "" });
              return;
            }
            t.current = window.setTimeout(() => {
              if (text !== lastSent.current) {
                lastSent.current = text;
                runSearch(text);
              }
            }, 250);
          }}
          onKeyDown={(e) => {
            if (e.key === "Enter" && q.trim()) {
              if (t.current) window.clearTimeout(t.current);
              lastSent.current = q.trim();
              runSearch(q.trim());
            }
          }}
        />
      </div>
      {searching && <div className="empty">searching...</div>}
      {noResults && <div className="empty">no results for that</div>}
      {results.length > 0 && q.trim().length > 0 && (
        <div className="resultlist">
          {results.map((r) => (
            <div className="rrow" key={r.id}>
              <TrackThumb id={r.id} className="rart" alt="" />
              <span className="rtitle" title={r.title}>{r.title}</span>
              <span className="rchan">{r.channel}</span>
              <span className="rdur mono">{r.durationLabel}</span>
              <button className="mini" onClick={() => pick(r, "play")} aria-label="play now">{"play"}</button>
              <button className="mini accent" onClick={() => pick(r, "queue")} aria-label="add to queue">{"+ queue"}</button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
});

function IconBtn({ title, onClick, children, label, className }: { title: string; onClick: () => void; children: React.ReactNode; label?: string; className?: string }) {
  return (
    <button className={"icon" + (className ? " " + className : "")} title={title} onClick={onClick} aria-label={label ?? title}>
      {children}
    </button>
  );
}

const FIELD_THEMES: { id: string; name: string }[] = [
  { id: "amber", name: "amber" },
  { id: "rose", name: "rose" },
  { id: "mint", name: "mint" },
  { id: "violet", name: "violet" },
  { id: "blue", name: "blue" },
  { id: "rgb", name: "rgb" },
];

const THEME_COLORS: Record<string, string> = {
  amber: "#ff9f1c",
  rose: "#ff5d7a",
  mint: "#3ddc97",
  violet: "#a06bff",
  blue: "#56a6ff",
  rgb: "linear-gradient(90deg, #ff9f1c, #ff5d7a, #a06bff, #56a6ff, #3ddc97)",
};

function Overlay({ show, label, role, onBackdrop, children }: {
  show: boolean;
  label: string;
  role?: string;
  onBackdrop?: () => void;
  children: React.ReactNode;
}) {
  const [render, setRender] = React.useState(show);
  const [on, setOn] = React.useState(false);
  React.useEffect(() => {
    if (show) {
      setRender(true);
      const r = requestAnimationFrame(() => requestAnimationFrame(() => setOn(true)));
      return () => cancelAnimationFrame(r);
    }
    if (!render) return;
    setOn(false);
    const t = window.setTimeout(() => setRender(false), 170);
    return () => window.clearTimeout(t);
  }, [show]);
  if (!render) return null;
  return (
    <div className={"overlay" + (on ? " show" : "")} role={role ?? "dialog"} aria-modal="true" aria-label={label}
      onMouseDown={(e) => { if (e.target === e.currentTarget) onBackdrop?.(); }}>
      {children}
    </div>
  );
}

function SetupWizard({ account, app, send, onSkip }: { account: AccountInfo | null; app: AppInfo | null; send: (m: Record<string, unknown>) => void; onSkip: () => void }) {
  const [tw, setTw] = React.useState(account?.twitchChannel ?? "");
  const [tt, setTt] = React.useState(account?.tiktokUser ?? "");
  const [cid, setCid] = React.useState("");
  const [sec, setSec] = React.useState("");
  const [cmd, setCmd] = React.useState(app?.command ?? "!sr");
  const [cook, setCook] = React.useState("");
  const [err, setErr] = React.useState("");

  const save = () => {
    if (!tw.trim() && !tt.trim()) {
      setErr("enter at least your twitch channel or tiktok username");
      return;
    }
    setErr("");
    send({
      type: "config",
      twitchChannel: tw.trim(),
      tiktokUser: tt.trim(),
      twitchClientId: cid.trim(),
      twitchClientSecret: sec.trim(),
      musicCommand: cmd.trim(),
      cookiesFile: cook.trim(),
    });
  };

  return (
    <>
      <div className="modal">
        <div className="modal-head">
          <span className="modal-title">first-time setup</span>
          <button className="modal-close" onClick={onSkip} aria-label="skip setup for now"><i className="fa-solid fa-xmark" aria-hidden="true" /></button>
        </div>
        <div className="modal-body">
          <p className="hint">
            connect your channels and the dashboard comes alive. a <span className="req">*</span> marks the fields that make chat work.
          </p>
          <div className="formrow">
            <label className="field-label" htmlFor="wz-tw">twitch channel <span className="req">*</span></label>
            <input id="wz-tw" className="text" value={tw} onChange={(e) => setTw(e.target.value)} placeholder="yourchannel" autoFocus />
          </div>
          <div className="formrow">
            <label className="field-label" htmlFor="wz-tt">tiktok username <span className="req">*</span></label>
            <input id="wz-tt" className="text" value={tt} onChange={(e) => setTt(e.target.value)} placeholder="yourname" />
          </div>
          <div className="formrow">
            <label className="field-label" htmlFor="wz-cid">twitch client id</label>
            <input id="wz-cid" className="text" value={cid} onChange={(e) => setCid(e.target.value)} placeholder="optional - works without it" />
          </div>
          <div className="formrow">
            <label className="field-label" htmlFor="wz-sec">twitch client secret</label>
            <input id="wz-sec" className="text" type="password" value={sec} onChange={(e) => setSec(e.target.value)} placeholder="optional" />
          </div>
          <div className="formrow">
            <label className="field-label" htmlFor="wz-cmd">chat request command</label>
            <input id="wz-cmd" className="text" value={cmd} onChange={(e) => setCmd(e.target.value)} />
          </div>
          <div className="formrow">
            <label className="field-label" htmlFor="wz-cook">youtube cookies file</label>
            <input id="wz-cook" className="text" value={cook} onChange={(e) => setCook(e.target.value)} placeholder="optional - full path to a cookies.txt" />
          </div>
          {err && <div className="form-err">{err}</div>}
          <div className="modal-actions">
            <button className="btn" onClick={save}>save and connect</button>
            <button className="btn ghost" onClick={onSkip}>skip for now</button>
          </div>
        </div>
      </div>
    </>
  );
}

function NumField({ id, value, onChange, min, max }: { id: string; value: string; onChange: (v: string) => void; min: number; max: number }) {
  const step = (d: number) => {
    const n = Math.floor(Number(value));
    const base = Number.isFinite(n) ? n : min;
    onChange(String(Math.min(max, Math.max(min, base + d))));
  };
  return (
    <div className="numwrap">
      <input id={id} className="text" inputMode="numeric" pattern="[0-9]*" value={value} onChange={(e) => onChange(e.target.value)} />
      <div className="numspin" aria-hidden="true">
        <button type="button" tabIndex={-1} onClick={() => step(1)}><i className="fa-solid fa-chevron-up" aria-hidden="true" /></button>
        <button type="button" tabIndex={-1} onClick={() => step(-1)}><i className="fa-solid fa-chevron-down" aria-hidden="true" /></button>
      </div>
    </div>
  );
}

function SettingsModal({ account, app, logs, logError, send, theme, onTheme, onClose }: {
  account: AccountInfo | null;
  app: AppInfo | null;
  logs: string[];
  logError: string | null;
  send: (m: Record<string, unknown>) => void;
  theme: string;
  onTheme: (t: string) => void;
  onClose: () => void;
}) {
  const [tab, setTab] = React.useState<"themes" | "account" | "config" | "logs" | "credits">("themes");
  const [tw, setTw] = React.useState(account?.twitchChannel ?? "");
  const [tt, setTt] = React.useState(account?.tiktokUser ?? "");
  const [cid, setCid] = React.useState("");
  const [sec, setSec] = React.useState("");
  const [cmd, setCmd] = React.useState(app?.command ?? "!sr");
  const [cook, setCook] = React.useState("");
  const [maxMin, setMaxMin] = React.useState(String(app?.maxTrackMinutes ?? 10));
  const [maxQ, setMaxQ] = React.useState(String(app?.maxQueueLength ?? 20));
  const [maxQuery, setMaxQuery] = React.useState(String(app?.maxQueryLength ?? 100));
  const [rateLimit, setRateLimit] = React.useState(String(app?.rateLimitSeconds ?? 15));
  const [cooldown, setCooldown] = React.useState(String(app?.globalCooldownSeconds ?? 5));
  const [requests, setRequests] = React.useState(app?.requestsOpen ?? true);
  const [err, setErr] = React.useState("");

  React.useEffect(() => {
    setTw(account?.twitchChannel ?? "");
    setTt(account?.tiktokUser ?? "");
    setCmd(app?.command ?? "!sr");
    if (app) {
      setMaxMin(String(app.maxTrackMinutes ?? 10));
      setMaxQ(String(app.maxQueueLength ?? 20));
      setMaxQuery(String(app.maxQueryLength ?? 100));
      setRateLimit(String(app.rateLimitSeconds ?? 15));
      setCooldown(String(app.globalCooldownSeconds ?? 5));
      setRequests(app.requestsOpen ?? true);
    }
  }, [account, app]);

  React.useEffect(() => {
    if (tab === "logs") send({ type: "logs" });
  }, [tab]);

  const save = () => {
    setErr("");
    send({
      type: "config",
      twitchChannel: tw.trim(),
      tiktokUser: tt.trim(),
      twitchClientId: cid.trim(),
      twitchClientSecret: sec.trim(),
      musicCommand: cmd.trim(),
      cookiesFile: cook.trim(),
    });
  };

  const num = (s: string, fallback: number) => {
    const n = Math.floor(Number(s));
    return Number.isFinite(n) ? n : fallback;
  };

  const saveMusic = () => {
    setErr("");
    send({
      type: "config",
      musicCommand: cmd.trim(),
      maxTrackMinutes: num(maxMin, 10),
      maxQueueLength: num(maxQ, 20),
      maxQueryLength: num(maxQuery, 100),
      rateLimitSeconds: num(rateLimit, 15),
      globalCooldownSeconds: num(cooldown, 5),
      requestsOpen: requests,
    });
  };

  return (
    <>
      <div className="modal">
        <div className="modal-head">
          <span className="modal-title">settings</span>
          <button className="modal-close" onClick={onClose} aria-label="close settings"><i className="fa-solid fa-xmark" aria-hidden="true" /></button>
        </div>
        <div className="tabs" role="tablist">
          {(["themes", "account", "config", "logs", "credits"] as const).map((t) => (
            <button key={t} className={"tab" + (tab === t ? " on" : "") + (t === "credits" ? " right" : "")} onClick={() => setTab(t)} role="tab" aria-selected={tab === t}>
              {t}
            </button>
          ))}
        </div>
        <div className="modal-body">
          {tab === "themes" && (
            <div className="swatches">
              {FIELD_THEMES.map((t) => (
                <button key={t.id} className={"swatch" + (theme === t.id ? " on" : "")} onClick={() => onTheme(t.id)}>
                  <span className="sw-dot" style={t.id === "rgb" ? { background: THEME_COLORS.rgb } : theme === t.id ? { background: THEME_COLORS[t.id] } : undefined} aria-hidden="true" />
                  {t.name}
                </button>
              ))}
            </div>
          )}
          {tab === "account" && (
            <>
              <div className="formrow">
                <label className="field-label" htmlFor="st-tw">twitch channel <span className="req">*</span></label>
                <input id="st-tw" className="text" value={tw} onChange={(e) => setTw(e.target.value)} placeholder="yourchannel" />
              </div>
              <div className="formrow">
                <label className="field-label" htmlFor="st-tt">tiktok username <span className="req">*</span></label>
                <input id="st-tt" className="text" value={tt} onChange={(e) => setTt(e.target.value)} placeholder="yourname" />
              </div>
              <div className="formrow">
                <label className="field-label" htmlFor="st-cid">twitch client id</label>
                <input id="st-cid" className="text" value={cid} onChange={(e) => setCid(e.target.value)} />
              </div>
              <div className="formrow">
                <label className="field-label" htmlFor="st-sec">twitch client secret</label>
                <input id="st-sec" className="text" type="password" value={sec} onChange={(e) => setSec(e.target.value)} />
              </div>
              <div className="formrow">
                <label className="field-label" htmlFor="st-cmd">chat request command</label>
                <input id="st-cmd" className="text" value={cmd} onChange={(e) => setCmd(e.target.value)} />
              </div>
              <div className="formrow">
                <label className="field-label" htmlFor="st-cook">youtube cookies file</label>
                <input id="st-cook" className="text" value={cook} onChange={(e) => setCook(e.target.value)} placeholder="full path to a cookies.txt" />
              </div>
              {err && <div className="form-err">{err}</div>}
              <div className="modal-actions">
                <button className="btn" onClick={save}>save account</button>
              </div>
            </>
          )}
          {tab === "config" && (
            <>
              <div className="formrow">
                <label className="field-label" htmlFor="st-cmd2">request word</label>
                <input id="st-cmd2" className="text" value={cmd} onChange={(e) => setCmd(e.target.value)} placeholder="!sr" />
                <p className="hint">what viewers type in chat to request a song.</p>
              </div>
              <div className="formrow">
                <label className="field-label" htmlFor="st-maxmin">longest song (minutes)</label>
                <NumField id="st-maxmin" value={maxMin} onChange={setMaxMin} min={1} max={60} />
                <p className="hint">requests longer than this get skipped.</p>
              </div>
              <div className="formrow">
                <label className="field-label" htmlFor="st-maxq">queue limit (songs)</label>
                <NumField id="st-maxq" value={maxQ} onChange={setMaxQ} min={1} max={100} />
                <p className="hint">how many songs can wait in line.</p>
              </div>
              <div className="formrow">
                <label className="field-label" htmlFor="st-rate">wait per viewer (seconds)</label>
                <NumField id="st-rate" value={rateLimit} onChange={setRateLimit} min={0} max={300} />
                <p className="hint">how long one viewer waits between their own requests.</p>
              </div>
              <div className="formrow">
                <label className="field-label" htmlFor="st-cool">wait between requests (seconds)</label>
                <NumField id="st-cool" value={cooldown} onChange={setCooldown} min={0} max={300} />
                <p className="hint">breathing room between any two requests from chat.</p>
              </div>
              <div className="formrow">
                <label className="field-label" htmlFor="st-maxquery">longest request text (characters)</label>
                <NumField id="st-maxquery" value={maxQuery} onChange={setMaxQuery} min={1} max={200} />
                <p className="hint">request text longer than this gets ignored.</p>
              </div>
              <div className="formrow">
                <span className="field-label">accept requests</span>
                <button className={"mini togg" + (requests ? " on" : "")} title="let viewers request songs" onClick={() => setRequests((r) => !r)}>
                  Requests {requests ? "on" : "off"}
                </button>
                <p className="hint">when off, song requests are ignored. mods can still skip.</p>
              </div>
              {err && <div className="form-err">{err}</div>}
              <div className="modal-actions">
                <button className="btn" onClick={saveMusic}>save music settings</button>
              </div>
            </>
          )}
          {tab === "logs" && (
            <>
              <div className="logbar">
                <button className="btn ghost" onClick={() => send({ type: "logs" })}>refresh</button>
                <ClearChat send={send} msg="logs" action="clear" label="clear" done="cleared" />
              </div>
              {logError && <div className="form-err">{logError}</div>}
              <pre className="logbox mono">{logs.length ? logs.join("\n") : "..."}</pre>
            </>
          )}
          {tab === "credits" && (
            <div className="credits">
              <img className="pfp" src="./pfp.png" alt="Vida" />
              <p>made by <span className="who">Vida.</span></p>
            </div>
          )}
        </div>
      </div>
    </>
  );
}