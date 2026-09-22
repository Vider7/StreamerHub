# StreamerHub

Everything your stream needs in one browser tab: chat from **Twitch** and **TikTok**
side by side, live numbers, and a music box your viewers can load songs into. It runs
on your PC and quietly lives in the system tray.

## What you get

- **Both chats in one column**, newest on top, with Twitch and TikTok badges and a
  star for mods.
- **Live stats**: viewers, likes, gifts (with diamond value), follows, shares, joins.
  They update as they happen.
- **Music**: your viewers type `!sr song name` in chat, the song lands in the queue,
  and it plays in a small background app with its own sound you capture in OBS. You
  control it all from the page: play, pause, previous song, skip, volume, and EQ.
- **Your layout, your way**: the chat, stats, and music panels can be dragged around
  by their title bars into any order. It remembers for next time.

## Get it running

1. Double-click `install.bat` (needs internet once).
   It sets the app up on your PC, adds a **"Streamer Hub"** icon to your Start Menu,
   and grabs the small music helper it needs. Your settings are never overwritten by
   updates.
2. Open **"Streamer Hub"** from the Start Menu.
   A page opens in your browser. That page is your dashboard, and the app itself stays
   in the system tray (bottom-right of your screen) so it never crowds your taskbar.
3. Point it at your accounts, just once:
   - Find the file `Config.json` in the app folder (it sits right next to
     `StreamerHub.exe`).
   - Change `Twitch.Channel` to your Twitch name and `TikTok.Username` to your TikTok
     name (no `@` needed).
   - Save the file, then open the app again.
4. The little lights in the top bar show when Twitch and TikTok are connected.

Updating later is the same: run `install.bat` again. It refreshes the app and keeps
your settings.

## Using the dashboard

- **Add a song**: click the search box in the music panel, type a song name, press
  Enter. Results appear with a **play** button (starts it now) and a **+ queue**
  button (adds it to the line). Every result shows a small cover preview.
- **Cover art**: the song that's playing shows its cover square next to the controls.
- **Jump around a song**: drag along the thin bar under the song title to move
  forward or backward. The arrow keys nudge 5 seconds too.
- **Keeps playing**: the music runs in the background app, not the page. Close the
  tab or the browser and it keeps going; open the page again and it shows exactly
  where it is.
- **Viewers add songs**: they write `!sr` plus a song name in chat, and it joins the
  queue automatically.
- **Controls**: play/pause, previous song, skip, and a volume slider live in the music
  panel.
- **EQ & loudness**: a slim 10-band equalizer sits under the player, plus a loudness
  switch for a fuller, broadcast-ready sound. Both are saved in your settings.
- **Clear chat**: hold the clear button in the chat panel (it fills up as you hold)
  to wipe the chat on screen.
- **Rearrange**: drag the panels by their title bars, chat / stats / music, in any
  order. It's saved as you go.
- **Open it again**: double-click the StreamerHub icon in the system tray.
- **Stop it**: right-click that icon and choose **Quit**. Your settings are saved.

## Your settings file

You normally never need to touch this, but if you want to change anything it's the
`Config.json` file beside the app. Here is what each setting means:

```
{
  "AppName": "StreamerHub",
  "Layout": "CSM",                  // panel order: C = chat, S = stats, M = music
  "Port": 51324,                    // the number used in the page address
  "AutoOpenBrowser": true,          // true = the page opens when the app starts
  "Twitch": {
    "Channel": "your-channel-name",  // your Twitch channel name
    "ClientId": "",                 // optional: for Twitch viewer count, see below
    "ClientSecret": "",             // optional: same
    "PollViewersSeconds": 45        // how often to count Twitch viewers
  },
  "TikTok": {
    "Username": "your-tiktok-name",   // your TikTok name, no @
    "CustomSigningServer": "",      // optional, for flaky TikTok connections
    "SigningServerApiKey": ""
  },
  "Music": {
    "Command": "!sr",               // the word in chat that requests a song
    "DefaultVolume": 25,            // how loud new songs start
    "MaxTrackMinutes": 10,          // longest song a request can be
    "MaxQueueLength": 20,           // song line limit
    "MaxQueryLength": 100,          // how long a request text can be
    "RateLimitSeconds": 15,         // wait between requests per viewer
    "GlobalCooldownSeconds": 5,     // pause between requests from chat
    "AutoNextRadio": true,          // true = a similar song plays when the queue runs out
    "MpvPath": "",                  // leave empty to use the mpv that install.bat puts in tools
    "AudioDevice": "",              // empty = your default sound output; set one for a specific device
    "Equalizer": [0,0,0,0,0,0,0,0,0,0], // 10-band EQ, each band -12 to +12
    "Loudness": false               // true = smooth out the sound for streaming
  }
}
```

Tip: change the file, save it, and reopen the app. Done.

## Twitch viewer count (optional, free)

The **Twitch viewers** number needs a small key from Twitch. Skip this and the app
still works completely: chat runs with no key at all, and the dashboard just shows
"viewer count off" for that one tile.

Want the number? Register an app at `https://dev.twitch.tv/console/apps`, then paste
its **Client ID** into `Twitch.ClientId` and its **Client Secret** into
`Twitch.ClientSecret`, save, and reopen the app.

## Putting the music on your stream

Music plays in its own small background app (mpv) that StreamerHub starts silently.
Because it is its own app, OBS can grab exactly that app's sound as a normal channel:

1. Play a song once (so the background player is running).
2. In OBS, add an **Application Audio Capture** source. On Windows 10/11 it's in
   Sources > Add under capture types, sometimes listed as "Audio Output Capture" until
   you choose an app from the list.
3. Pick the **mpv** entry (labeled as the music player) from the app list.
4. The song now has its own fader, mute button, and audio-bus routing in OBS, just
   like any other source. Make the song volume on the dashboard fairly high and use
   the OBS fader for your stream level.

If Application Audio Capture doesn't appear on your version of Windows, fall back to
**Audio Output Capture** (desktop sound) or set `Music.AudioDevice` in Config.json to
send music to its own output device. There's nothing to put on screen: the player has
no window, so no screen capturing is involved.

## Song requests from chat

- Your viewers type `!sr` plus the name of a song, StreamerHub finds it on YouTube,
  adds it to the queue, and it plays right away in the background player.
- Links are not accepted in requests, only song names. Keeps chat requests simple and
  safe.
- When the queue runs out, the radio finds **one** similar song to follow it. Turn
  "radio off" in the music panel if you would rather it stop.
- Requests are skipped if they are longer than `MaxTrackMinutes`, if the queue is
  full, or if the same viewer asks again too fast.
- The bot stays quiet: the only bot lines that appear in the dashboard feed are song
  requests. None of the playback chatter is posted, and the app never writes into
  your Twitch or TikTok chat.

## If something looks wrong

- Watch the small status text at the bottom of the page and the lights in the top bar.
  They say exactly what's happening, for example "TikTok: connecting".
- Heard no music? Make sure `mpv.exe` is in the `tools\mpv` folder next to the app
  (`install.bat` puts it there), then play a song.
- There's a simple log file at `logs\app.log` in the app folder. If you ask a friend
  for help, sending that file is usually everything they need.

## For the curious

- The whole app is one exe plus a web page in the same folder. Copy the whole folder
  to another PC and it runs there too.
- `install.bat` installs and updates. `build.bat` rebuilds from source if you ever
  want to build it yourself.
- The `tools` folder holds the two helpers: `yt-dlp.exe` (finding songs) and the `mpv`
  folder (making the sound).
- It's a small local server (StreamerHub.exe) that opens
  `http://127.0.0.1:51324` in your browser. Internet is only needed for the chat
  feeds, YouTube search, and audio; everything else stays on your PC.