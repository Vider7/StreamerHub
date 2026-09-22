# repro tools (not part of the app)

- `PipeRepro/`: minimal console app that reproduces mpv IPC behavior over a
  named pipe (pause / time-pos observation). Used while debugging `MpvPlayer`.
  Run from the repo root: `dotnet run --project tools/repro/PipeRepro`
  (optionally pass an mpv path as the first arg, `nullaudio` as the second).
