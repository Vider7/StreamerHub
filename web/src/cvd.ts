// Colorblind (CVD) color conversion. Pure functions, no imports, no DOM.
// The overlay (web/public/overlay.html) carries a type-stripped copy of this file. Keep them identical.

export type CvdMode = "off" | "protan" | "deutan" | "tritan";
type V3 = [number, number, number];

// Machado 2009, severity 1.0, applied in LINEAR sRGB. Used only to MEASURE how a color is seen.
const SIM: Record<string, V3[]> = {
  protan: [[0.152286, 1.052583, -0.204868], [0.114503, 0.786281, 0.099216], [-0.003882, -0.048116, 1.051998]],
  deutan: [[0.367322, 0.860646, -0.227968], [0.280085, 0.672501, 0.047413], [-0.01182, 0.04294, 0.968881]],
  tritan: [[1.255528, -0.076749, -0.178779], [-0.078411, 0.930809, 0.147602], [0.004733, 0.691367, 0.3039]],
};

const BG_HEX = "#0a0a0c";   // app --bg, accents must stay readable on it
const KEEP = 0.6;           // seen chroma must keep >= 60% of the picked chroma
const MIN_SEEN_C = 0.07;    // ...and must not collapse to gray
const MIN_CONTRAST = 4.5;   // vs BG_HEX
const MIN_PAIR_DE = 0.12;   // min distance between two roles as SEEN by the deficiency

const clamp01 = (v: number): number => Math.min(1, Math.max(0, v));
const s2l = (c: number): number => (c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4));
const l2s = (c: number): number => (c <= 0.0031308 ? c * 12.92 : 1.055 * Math.pow(c, 1 / 2.4) - 0.055);

export function isHex6(s: unknown): s is string {
  return typeof s === "string" && /^#[0-9a-f]{6}$/i.test(s);
}
function hexToLin(h: string): V3 {
  const n = parseInt(h.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255].map((v) => s2l(v / 255)) as V3;
}
function linToHex(l: V3): string {
  return "#" + l.map((v) => Math.round(l2s(clamp01(v)) * 255).toString(16).padStart(2, "0")).join("");
}
function linToLab([r, g, b]: V3): V3 {
  const l = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
  const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
  const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
  return [
    0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s,
    1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s,
    0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s,
  ];
}
function labToLin([L, a, b]: V3): V3 {
  const l = (L + 0.3963377774 * a + 0.2158037573 * b) ** 3;
  const m = (L - 0.1055613458 * a - 0.0638541728 * b) ** 3;
  const s = (L - 0.0894841775 * a - 1.291485548 * b) ** 3;
  return [
    4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  ];
}
const labToLch = ([L, a, b]: V3): V3 => [L, Math.hypot(a, b), (Math.atan2(b, a) * 180 / Math.PI + 360) % 360];
const lchToLab = ([L, C, h]: V3): V3 => [L, C * Math.cos(h * Math.PI / 180), C * Math.sin(h * Math.PI / 180)];
const inGamut = (l: V3): boolean => l.every((v) => v >= -1e-4 && v <= 1 + 1e-4);
const lum = (l: V3): number => 0.2126 * clamp01(l[0]) + 0.7152 * clamp01(l[1]) + 0.0722 * clamp01(l[2]);
function contrast(a: V3, b: V3): number {
  const x = lum(a) + 0.05, y = lum(b) + 0.05;
  return x > y ? x / y : y / x;
}
function simulate(lin: V3, mode: string): V3 {
  const m = SIM[mode];
  return [0, 1, 2].map((i) => m[i][0] * lin[0] + m[i][1] * lin[1] + m[i][2] * lin[2]) as V3;
}
const dist = (a: V3, b: V3): number => Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2]);

// Largest chroma <= C that is still inside sRGB at this L and hue.
function fitChroma(L: number, C: number, h: number): number {
  let lo = 0, hi = C, best = 0;
  for (let i = 0; i < 24; i++) {
    const mid = (lo + hi) / 2;
    if (inGamut(labToLin(lchToLab([L, mid, h])))) { best = mid; lo = mid; } else hi = mid;
  }
  return best;
}
interface Cand { L: number; C: number; h: number; lin: V3 }
function build(L: number, C: number, h: number): Cand {
  const c = fitChroma(L, C, h);
  return { L, C: c, h, lin: labToLin(lchToLab([L, c, h])) };
}
function seenLab(c: Cand, mode: string): V3 {
  return linToLab(simulate(c.lin, mode));
}
function isSafe(c: Cand, mode: string): boolean {
  const seenC = labToLch(seenLab(c, mode))[1];
  const ratio = c.C > 1e-4 ? seenC / c.C : 1;
  return ratio >= KEEP && seenC >= Math.min(MIN_SEEN_C, c.C * KEEP);
}

/**
 * Convert ONE picked color into its CVD-safe version for `mode`.
 * - Starts from the picked color (hue, lightness, chroma).
 * - If that hue is already perceivable for the deficiency it comes back UNCHANGED.
 * - Otherwise the hue moves the SMALLEST amount needed (search outward 1 degree at a time,
 *   both directions) to the nearest hue the deficiency can still perceive. Lightness and
 *   chroma are kept. Then lightness is lifted only if contrast vs the app background < 4.5.
 * - `avoidHex` (optional): another already-resolved role color. The result must also look at least
 *   MIN_PAIR_DE away from it AS SEEN by the deficiency (used for ok/down and TW/TT pairs).
 * mode "off" or an invalid hex returns the input untouched.
 */
export function resolveCvd(hex: string, mode: string, avoidHex?: string): string {
  if (!isHex6(hex) || !SIM[mode]) return hex;
  const [L0, C0, h0] = labToLch(linToLab(hexToLin(hex)));
  if (C0 < 0.02) return hex; // white/gray/black stay neutral
  const avoid = avoidHex && isHex6(avoidHex) ? linToLab(simulate(hexToLin(avoidHex), mode)) : null;
  let found: Cand | null = null;
  for (let d = 0; d <= 180 && !found; d++) {
    for (const sgn of d === 0 ? [1] : [1, -1]) {
      const c = build(L0, C0, (h0 + sgn * d + 360) % 360);
      if (!isSafe(c, mode)) continue;
      if (avoid && dist(seenLab(c, mode), avoid) < MIN_PAIR_DE) continue;
      found = c;
      break;
    }
  }
  let c = found ?? build(L0, C0, h0);
  const bg = hexToLin(BG_HEX);
  let L = c.L;
  while (L < 0.98 && contrast(build(L, c.C, c.h).lin, bg) < MIN_CONTRAST) L += 0.01;
  if (L !== c.L) c = build(L, c.C, c.h);
  return linToHex(c.lin);
}

/** Hex of how a person with `mode` sees `hex`. For the live "as seen" preview only, never for the UI palette. */
export function seenAs(hex: string, mode: string): string {
  if (!isHex6(hex) || !SIM[mode]) return hex;
  return linToHex(simulate(hexToLin(hex), mode));
}

/** Lighten/darken in OKLab for accent-hover / accent-down. amt is +/- lightness, e.g. 0.06 / -0.06. */
export function shiftL(hex: string, amt: number): string {
  if (!isHex6(hex)) return hex;
  const [L, C, h] = labToLch(linToLab(hexToLin(hex)));
  return linToHex(build(Math.min(0.99, Math.max(0.05, L + amt)), C, h).lin);
}

export interface CvdPalette { accent: string; hover: string; down: string; tw: string; tt: string; ok: string; bad: string }

/** Everything the UI needs, from the user's CURRENT accent hex. Base role colors are the app defaults. */
export function cvdPalette(accentHex: string, mode: string): CvdPalette {
  const accent = resolveCvd(accentHex, mode);
  const bad = resolveCvd("#f87171", mode);              // --red, offline
  const ok = resolveCvd("#4ade80", mode, bad);          // --green, online, must differ from bad
  const tt = resolveCvd("#fe2c55", mode);               // TikTok tag
  const tw = resolveCvd("#a06bff", mode, tt);           // Twitch tag, must differ from tt
  return { accent, hover: shiftL(accent, 0.06), down: shiftL(accent, -0.06), tw, tt, ok, bad };
}
