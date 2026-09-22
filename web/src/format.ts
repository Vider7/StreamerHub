export function fmt(n: number): string {
  if (!Number.isFinite(n)) return "-";
  return Math.round(n).toLocaleString("en-US");
}

export function fmtClock(sec: number): string {
  if (!Number.isFinite(sec) || sec < 0) sec = 0;
  const t = Math.floor(sec);
  const h = Math.floor(t / 3600);
  const m = Math.floor((t % 3600) / 60);
  const s = t % 60;
  const mm = m.toString().padStart(2, "0");
  const ss = s.toString().padStart(2, "0");
  return (h > 0 ? h + ":" : "") + mm + ":" + ss;
}