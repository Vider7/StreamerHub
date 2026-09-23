# Design direction

Product: a desktop companion for a streamer who runs, a multistreaming musician, one Twitch + one TikTok.
Job of the screen: monitor both chats, watch live numbers, and run the request-driven music in one window.

Reading this as: a live companion dashboard for a musician who streams, dark and quiet so the stream stays the focal point, dial ENERGY 2 / RHYTHM 2 / MOTION 1.

## Identity

- Tool used for hours under a bright display (OBS, editor, game). It must sit in the background: calm, read in a glance, quick to check.
- Personality: quiet utility with one colored voice (the accent), like a broadcast rack that is never the show itself.

## Dials

- ENERGY 2: no hero moves, no gradients. Flat surfaces, but crafted: custom scrollbars and slider, stat grids divided by hairlines with one raised focal tile, a slightly louder hello than a bare rack.
- RHYTHM 2: three columns, predictable placement, with deliberate breaks: the LIVE viewers tile is the filled focal tile, and LIVE + NOW PLAYING carry the orange live-dot gesture. Uniform where the streamer re-finds numbers, accented where the eye should go.
- Panels are a fixed rack; the streamer drags a panel by its title bar to reorder chat/stats/music any way, persisted in Config.json. Free drag on a three-slot grid keeps the rack predictable while letting the streamer dock what they watch most to the left.
- Queue rows drag to reorder; the divider above the activity feed drags vertically to resize it, remembered per browser.
- The playback state never lies: with nothing loaded the player reads STOPPED, paused reads PAUSED, and errors advance the queue. Sound never comes from the page: a background mpv subprocess owns the audio so OBS captures it as its own app, and the dashboard is a remote control (R-04).
- The next song preloads fully to local disk while the current plays (the radio pick is chosen before the current ends, not after), and the transition serves from disk, so tracks start with no gap.
- The transport is previous / play-pause / skip, on screen and on the keyboard media keys (mpv leaves media keys alone so every press flows through the queue); scrubbing is done on the timeline bar (drag or arrow keys), never with a back-10 button (R-26).
- Album art is decorative: never draggable or selectable, with a flat ♪ fallback tile (R-27).
- A slim 10-band EQ and a loudness switch sit under the player, applied through mpv's audio filters and saved in Config.json.
- MOTION 1: static except cursor/hover state changes. Drag uses the native drag glow only on hover and a drop-target outline.
- Chat reads newest-first (newest on top), matching how the streamer watches the feed.

## Palette

Brand the streamer chose: orange, purple, black.

- Base: #0A0A0C (window, black). Panels: #121014. Raised: #1A181D. Borders: #2B2535. Hairlines: #1C1822.
- Text primary: #E7E4EC (warm white). Text dim: #9C95A8 (passes WCAG AA on panel, ratio ~6:1).
- Accent orange: #FF9F1C. Secondary purple: #A06BFF. Both pass AA on black.
- Platform tags: Twitch = purple #A06BFF, TikTok = red #FE2C55, bot = lavender. Both brand colors pass AA on black.
- Functional status only, kept to tiny dots/badges (not brand): ok #2EBD85, warn #F5A524, bad #E5484D.
- Why: the black base keeps the window invisible next to OBS output; orange is the accent that means "active, live, do this now"; purple and red carry the two platform identities in their own lanes so they never compete (R-29).

## Typography

- Segoe UI everywhere. Reason: native Windows face, crisp at 12-13px live read-out sizes, familiar to the friend, matches the reference tool identity.
- Uppercase micro labels with 1.2 tracking on section heads only. Reason: consistent with the reference overlay identity, marks these as instrument read-outs (counters, status) not prose. (R-06)
- Mono reserved for nothing decorative.

## Components

- Flats: no glow, no shadows; elevation shown only by border color (R-12, R-13).
- Corner radius: 10px on panels, 8px on buttons and inputs, 6px on list rows and controls; round surfaces are reserved for the live/status dots and the circular play button. Nothing else is pill-shaped (R-11).
- Icons are Font Awesome, bundled locally via npm (no CDN, the dashboard works offline). Reason: owner chose FA as the single icon language; transport uses the slimmed treatment (smaller, dim idle) so it stays quiet (R-04, R-31).
- TikTok fanclub badges render next to member names, using the badge image TikTok sends for their club level. Reason: the streamer asked to see club stickers at a glance; only members get one (R-23, R-31).
- One accent at a time: the active filter tab, primary action, live status, progress bar, bot tag (R-29).
- Per-user chat colors stay inside the family: muted oranges, ambers, mauves, lavenders, plums. Cohesion with the theme, no rainbow; red is never used for a user so the TikTok tag owns it.
- Album art: the playing song shows a square cover tile; small tiles in queue and search rows. Fallback is a flat ♪ tile, never a broken image (R-27).
- The seek bar is a timeline, not a dial: drag it to scrub, arrow keys nudge 5s, and reopening the page shows the live position reported by the background player (R-26).
- Clear chat is destructive, so it needs a confirm: the button fills while the pointer (or Enter/Space) is held and only then fires (R-28, R-32).
- No text selection anywhere except where copying is useful: the chat feed, the song title, and text inputs. Controls and readouts never highlight like text (R-26).

## Do not

- No gradients, no orbs, no grid backgrounds, no left color stripes that carry no state (R-01, R-07).
- No decorative emoji, no arrows on buttons for decoration (R-04, R-08).
- No fake numbers: every counter is wired to real events or reads the platform's reported values.