import { useEffect, useRef, useState } from "react";

export type TrackResultShape = {
  id: string;
  title: string;
  channel: string;
  duration: number;
  durationLabel: string;
};

export type Track = TrackResultShape & { by: string; platform?: string };

export type ChatEntry = {
  time: string;
  role: "chat" | "bot";
  platform?: "twitch" | "tiktok";
  user: string;
  msg: string;
  color: string;
  tag: string;
  isMod: boolean;
  isBroad: boolean;
  fanclubBadge?: string;
  fanclubName?: string;
  fanclubLevel?: number;
  _k?: number;
};

export type ActivityEntry = {
  time: string;
  kind: string;
  text: string;
  color: string;
  _k?: number;
};

export type AppInfo = {
  name: string;
  layout: string;
  command: string;
  maxTrackMinutes: number;
  tiktokUser: string;
  twitchChannel: string;
  volume: number;
};

export type ConnInfo = {
  twitch: { ok: boolean; detail: string };
  tiktok: { ok: boolean; detail: string };
};

export type Stats = {
  total: number;
  twitch: number;
  tiktok: number;
  twitchPerMin: number;
  tiktokPerMin: number;
  twitchViewers: number;
  viewers: number;
  peakViewers: number;
  likes: number;
  totalLikes: number;
  gifts: number;
  giftValue: number;
  follows: number;
  shares: number;
  joins: number;
};

export type Music = {
  now: Track | null;
  queue: Track[];
  liked: Track[];
  history: Track[];
  blocked: Track[];
  volume: number;
  position: number;
  playing: boolean;
  radio: boolean;
  eq: number[];
  loudness: boolean;
};

export type UpdateInfo = {
  current: string;
  status: string;
  latest: string;
  ready: boolean;
};

export type AccountInfo = {
  twitchChannel: string;
  tiktokUser: string;
};

export type SetupInfo = {
  required: boolean;
};

export type HubState = {
  connected: boolean;
  app: AppInfo | null;
  conn: ConnInfo | null;
  setup: SetupInfo | null;
  account: AccountInfo | null;
  twitchViewers: number;
  chat: ChatEntry[];
  activity: ActivityEntry[];
  stats: Stats | null;
  music: Music | null;
  pos: { position: number; playing: boolean };
  notice: string;
  applying: boolean;
  results: TrackResultShape[];
  layout: string;
  update: UpdateInfo;
  logs: string[];
  logError: string | null;
};

const emptyState: HubState = {
  connected: false,
  app: null,
  conn: null,
  setup: null,
  account: null,
  twitchViewers: -1,
  chat: [],
  activity: [],
  stats: null,
  music: null,
  pos: { position: 0, playing: false },
  notice: "",
  applying: false,
  results: [],
  layout: "CSM",
  update: { current: "", status: "idle", latest: "", ready: false },
  logs: [],
  logError: null,
};

let chatSeq = 0;
let noticeSeq = 0;

function nowTime() {
  const d = new Date();
  return (
    d.getHours().toString().padStart(2, "0") +
    ":" +
    d.getMinutes().toString().padStart(2, "0") +
    ":" +
    d.getSeconds().toString().padStart(2, "0")
  );
}

export function useHub() {
  const wsRef = useRef<WebSocket | null>(null);
  const [state, setState] = useState<HubState>(() => ({
    ...emptyState,
    applying: localStorage.getItem("sh.updating") === "1",
  }));
  const send = (msg: Record<string, unknown>) => {
    const ws = wsRef.current;
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(msg));
  };

  useEffect(() => {
    let closed = false;
    let retry = 0;
    let timer: number | undefined;
    let hb: number | undefined;
    let lastPong = 0;

    const open = () => {
      if (closed) return;
      const ws = new WebSocket((location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/ws");
      wsRef.current = ws;

      ws.onopen = () => {
        retry = 0;
        lastPong = Date.now();
        setState((s) => ({ ...s, connected: true }));
      };

      ws.onmessage = (ev) => {
        let msg: Record<string, unknown>;
        try {
          msg = JSON.parse(ev.data as string);
        } catch {
          return;
        }
        handleMessage(msg);
      };

      ws.onclose = () => {
        setState((s) => ({
          ...s,
          connected: false,
          pos: { ...s.pos, playing: false },
        }));
        if (closed) return;
        retry = Math.min(retry + 1, 5);
        timer = window.setTimeout(open, retry * 1000);
      };
    };

    const handleMessage = (msg: Record<string, unknown>) => {
      switch (msg.type) {
        case "init": {
          const i = msg as Record<string, unknown>;
          if (localStorage.getItem("sh.updating") === "1") {
            localStorage.removeItem("sh.updating");
            window.location.reload();
            break;
          }
          localStorage.removeItem("sh.updating");
          setState({
            connected: true,
            applying: false,
            app: i.app as AppInfo,
            conn: (i.conn as ConnInfo) ?? null,
            twitchViewers: typeof i.twitchViewers === "number" ? (i.twitchViewers as number) : -1,
            chat: (i.chat as ChatEntry[] ?? []).map((e) => ({ ...e, _k: chatSeq++ })),
            activity: (i.activity as ActivityEntry[] ?? []).map((e) => ({ ...e, _k: chatSeq++ })),
            stats: (i.stats as Stats) ?? null,
            music: (i.music as Music) ?? null,
            pos: ((i.music as Music)?.position != null
              ? { position: (i.music as Music)?.position ?? 0, playing: (i.music as Music)?.playing ?? false }
              : { position: 0, playing: false }),
            notice: (i as Record<string, string>).notice ?? "",
            results: [],
            layout: ((i.app as AppInfo)?.layout ?? "CSM") as string,
            update: (i.update as UpdateInfo) ?? { current: "", status: "idle", latest: "", ready: false },
            setup: (i.setup as SetupInfo) ?? null,
            account: (i.account as AccountInfo) ?? null,
            logs: [],
            logError: null,
          });
          break;
        }
        case "config":
          break;
        case "logs":
          setState((s) => ({
            ...s,
            logs: (msg.lines as string[] ?? []),
            logError: (msg.error as string | null) ?? null,
          }));
          break;
        case "chat":
          {
          const e = msg.entry as ChatEntry;
          if (!e) break;
          setState((s) => ({ ...s, chat: [...s.chat.slice(-599), { ...e, _k: chatSeq++ }] }));
          break;
        }
        case "chat-clear":
          setState((s) => ({ ...s, chat: [] }));
          break;
        case "activity-clear":
          setState((s) => ({ ...s, activity: [] }));
          break;
        case "activity": {
          const text = msg.text as string;
          const color = (msg.color as string) ?? "#E7E4EC";
          const kind = (msg.kind as string) ?? "system";
          setState((s) => ({
            ...s,
            activity: [...s.activity.slice(-199), {
              time: nowTime(), kind, text, color, _k: chatSeq++,
            }],
          }));
          break;
        }
        case "stats":
          setState((s) => ({ ...s, stats: msg.stats as Stats }));
          break;
        case "music": {
          const m = msg.music as Music;
          setState((s) => ({
            ...s,
            music: m,
            pos: m ? { position: m.position, playing: m.playing } : s.pos,
          }));
          break;
        }
        case "music-time":
          setState((s) => ({
            ...s,
            pos: { position: typeof msg.position === "number" ? (msg.position as number) : 0, playing: !!msg.playing },
          }));
          break;
        case "notice": {
          const id = ++noticeSeq;
          const text = msg.text as string;
          const applying = text.startsWith("installing update");
          if (applying) localStorage.setItem("sh.updating", "1");
          setState((s) => ({ ...s, notice: text, applying: s.applying || applying }));
          window.setTimeout(() => {
            setState((s) => (s.notice === (msg.text as string) ? { ...s, notice: "" } : s));
          }, 7000);
          break;
        }
        case "conn":
          setState((s) => ({ ...s, conn: msg as unknown as ConnInfo }));
          break;
        case "twitch-viewers":
          setState((s) => ({ ...s, twitchViewers: msg.value as number }));
          break;
        case "search":
          setState((s) => ({ ...s, results: (msg.results as TrackResultShape[] ?? []) }));
          break;
        case "layout":
          setState((s) => ({ ...s, layout: (msg.order as string) ?? s.layout }));
          break;
        case "radio":
          setState((s) => ({ ...s, music: s.music ? { ...s.music, radio: msg.on as boolean } : s.music }));
          break;
        case "update":
          setState((s) => ({ ...s, update: msg as unknown as UpdateInfo }));
          break;
        case "pong":
          lastPong = Date.now();
          break;
      }
    };

    open();
    hb = window.setInterval(() => {
      if (closed) return;
      const ws = wsRef.current;
      if (!ws || ws.readyState !== WebSocket.OPEN) return;
      if (Date.now() - lastPong > 20000) {
        try {
          ws.close();
        } catch {
          // ignore
        }
      } else {
        ws.send(JSON.stringify({ type: "ping", t: Date.now() }));
      }
    }, 15000);
    return () => {
      closed = true;
      if (timer) window.clearTimeout(timer);
      if (hb) window.clearInterval(hb);
      try {
        wsRef.current?.close();
      } catch {
        // ignore
      }
    };
  }, []);

  return { state, send };
}