# dev probes (not part of the app)

Loose WebSocket test scripts, kept for debugging the dashboard protocol:

- `pauseprobe.cjs`, `pp2.cjs`, `pp3.cjs`: play a song over `/ws`, pause after a
  few seconds, log positions. One-off pause-behavior checks.
- `_stress.mjs`: hammers `/ws` with 40 clients plus 80 short-lived connections.

Run from the repo root, e.g. `node web/dev-probes/pp3.cjs`.
They need the `ws` package (already present as a Vite transitive dep) and the
server running on `ws://127.0.0.1:52100/ws` (port in the scripts may differ from
`Config.json`; edit the URL at the top of the file if needed).
