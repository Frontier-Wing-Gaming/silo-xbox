# Silolux for Xbox

A native Xbox (UWP, C#/XAML) client for [Silo](https://github.com/Silo-Server/silo-server):
sign-in, profiles, home rows, movie and series pages, and playback through Media Foundation, all
on Silo's native `/api/v2` and playback protocol v3. It also runs on Windows desktops.

Status: early prototype, `0.2.1`.

Silolux is an independent project by Frontier Wing Gaming. It is not made, endorsed or supported
by Silo Media L.L.C.; "Silo" is their trademark and is used here only to say what this app connects to.

## Features

| Area | Behavior |
| --- | --- |
| Connect | Server address entry, checked against `/api/v2/system/info` (`api_major` 2). |
| Sign-in | Device sign-in: the console shows a code, you approve it at `<server>/activate` on another device. A username and password form sits alongside. The refresh token is kept in the Credential Locker and rotated in a single flight. |
| Profiles | Picker with PIN entry (`verify-pin`). One profile without a PIN is picked automatically. |
| Home | `/home/sections` rows. Continue Watching style rows use landscape art and progress bars. |
| Details | Movies and episodes get Resume / Play from start. Series get a season strip, an episode list, and Play next episode from `play_content_id`. |
| Playback | `MediaPlayer` with `AdaptiveMediaSource` for HLS. Progress is sequenced every 10 s and on pause, with a stop on exit. A decode or network failure triggers a `failure_recovery` replan, up to 8 attempts. Play from start always starts at 0 (the server would otherwise resume). The server's audio pick is applied on original files. |
| Subtitles | Sidecar text subtitles load as `TimedTextSource`, only the chosen track at start and the others when picked. ASS/SSA tracks are fetched as WebVTT (the server converts by extension), since the console can't draw ASS styling anyway. On a stream the server started mid-film, the app shifts cue times to the stream's clock. |
| Seeking | D-pad left/right while the controls are hidden, or LB/RB at any time, skip 10 s back or 30 s forward, with the film's time in a strip at the top; the player's seek bar works as usual too. Media Foundation ignores a start time for HLS, so the app seeks to the plan's start once the stream opens. A server remux (`can_seek_anywhere` false) has no length the player can seek in: there the app hides the seek bar and skip buttons, and each run of skips becomes one `seek_reanchor` replan. |
| Display | On Xbox the TV switches to a refresh rate that fits the film (24 Hz for 23.976/24 fps, 50 Hz for 25 fps; needs *Allow 24 Hz* in the console's video modes) and to HDR10 for HDR video, then back on exit. |
| Quality | Away from the local network a short speed test is sent as `bandwidth_estimate_kbps`, so *auto* quality fits the connection. The player menu also lists the server's fixed quality rungs. |
| Audio | When the server's progressive route would downmix 5.1+ audio the console can't decode (DTS, TrueHD) to stereo, and the console's audio output has more than two channels, the app asks for the HLS route, which keeps 5.1. |
| Capabilities | Decoders are probed with `CodecQuery` (H.264, HEVC, VP9, AV1, MPEG-2, VC-1; AAC, MP3, AC-3, E-AC-3, FLAC, ALAC, Opus). HDR10 is reported only when the console offers the app an HDR (SMPTE 2084) display mode. No passthrough or Dolby Vision claims. |
| Diagnostics | *Diagnostics* on the home header lists decoders, display modes, TV colour, audio output and network next to what the app reports to the server, plus the last playback (delivery, recipe, decoded size, display match). Saved as `LocalState\diagnostics.txt` and `playback.txt`. |

Media URLs carry the account bearer as `token` only for server-relative paths, as the web player
does. Absolute node or proxy URLs never get it.

## Build

Needs Visual Studio 2022 or later with the **Universal Windows Platform development** workload.

```powershell
# Debug (deploys to this PC from Visual Studio: open Silolux\Silolux.csproj, F5)
msbuild Silolux\Silolux.csproj /restore /p:Configuration=Debug /p:Platform=x64

# Release (.NET Native), produces Silolux\AppPackages\...\Silolux_0.2.0.0_x64.msix
msbuild Silolux\Silolux.csproj /restore /p:Configuration=Release /p:Platform=x64
```

Regenerate tiles from an icon with `scripts/make-assets.ps1 -Source <icon.png>`.

Testing on a PC that also runs the server: UWP apps can't connect to their own machine
(including `localhost` and the PC's LAN IP) until exempted. Run once, elevated:

```powershell
# Quote the argument: PowerShell splits -n=... at the dot otherwise.
CheckNetIsolation LoopbackExempt -a '-n=<PackageFamilyName>'
```

Use `http://127.0.0.1:<port>`, not `localhost`: `localhost` resolves to `::1` first, Docker
Desktop's port forwarding answers on IPv4 only, and Windows.Web.Http does not fall back.

With WSL `networkingMode=Mirrored`, Docker ports have no Windows-side listener and the
exemption does not reach them. Put a plain TCP relay on Windows in front (any process that
listens on, say, `127.0.0.1:8091` and forwards to the server port) and connect to that.

The suffix comes from the signing publisher; `Get-AppxPackage FrontierWingGaming.Silolux` shows the exact
`PackageFamilyName`. A console reaching a server on another machine needs no exemption.

## Run on an Xbox

1. Turn on **Developer Mode** on the console (Xbox Dev Mode app, one-time Partner Center
   developer account).
2. Either deploy from Visual Studio (*Remote Machine*, the console's IP, authentication
   *Universal (Unencrypted Protocol)*; pair with the PIN from Dev Home), or
3. install a release package: download the zip from a GitHub release, open Device Portal at
   `https://<xbox-ip>:11443`, choose *Add*, pick the `.msix`, then add the three packages in
   `Dependencies\x64` when it asks for dependencies.

The device sign-in code, controller keyboard and layout are sized for the console's
960x540 layout (`Silolux/Services/Ui.cs` holds the TV sizes). If the app crashes, details
land in the Visual Studio output (`Unhandled:`) and in `LocalState\crash.log`, which Device
Portal's file explorer can open.

## Releases

Set the version in `Silolux/Package.appxmanifest` (`Identity Version`, such as `0.2.0.0`), then
push a tag such as `v0.2.0`. The Release workflow builds a signed sideload package and
attaches a zip (the `.msix`, its `.cer`, the runtime packages it needs, and Microsoft's
install scripts) to a draft GitHub release for you to review and publish.

Packages are signed with the certificate in the repository secrets `XBOX_SIGNING_PFX` (the
`.pfx`, base64) and `XBOX_SIGNING_PASSWORD` when they exist, otherwise with a one-off
self-signed certificate per run. Set the secrets so updates install over each other: a
package signed with a different certificate has to be uninstalled first. The certificate's
subject must be the manifest's `Publisher`, `CN=FrontierWingGaming`. On a PC, trust the
`.cer` (Local Machine, Trusted People) before installing.

## Microsoft Store

1. Reserve "Silolux" in Partner Center, then copy the three values on *Product management →
   Product identity* into repository variables (Settings → Secrets and variables → Actions →
   Variables): `STORE_IDENTITY_NAME`, `STORE_PUBLISHER` and `STORE_PUBLISHER_DISPLAY_NAME`.
2. Run the *Store package* workflow (or push a `v*` tag). It applies that identity with
   `scripts/set-store-identity.ps1` and builds the `.msixupload` to upload in the submission.
   The Store signs it; the sideload certificate is not used. To build one locally, run the
   script, build with `/p:UapAppxPackageBuildMode=StoreUpload`, and don't commit the changed
   manifest.
3. Run the Windows App Certification Kit before submitting, from an administrator
   PowerShell: `scripts\run-wack.ps1 -Package <bundle>`, where the bundle is the workflow's
   `silolux-xbox-wack-bundle` artifact (the build's `*_Test\*.msixbundle`). Don't test the
   `.msixupload`: it holds IL that the Store compiles with .NET Native after submission, so it
   can't launch locally. The script signs a copy with a temporary certificate (the kit only
   installs signed packages), runs the kit, lists anything that didn't pass, and removes the
   certificate and test install afterwards.

The manifest declares the restricted capability `hevcPlayback`. On Xbox it is what makes the
console offer an app HDR10 (and 4K) display modes; without it HDR video plays tone-mapped in SDR.
It also raises the app's memory limit and means the app is closed, not suspended, when a game
starts. Partner Center asks for a justification in the submission's *Submission options*:
"Video playback of the user's HEVC/HDR10 media from their own media server; the capability is
needed for HDR10 and 4K display modes."

Privacy policy URL for the listing:
https://github.com/Frontier-Wing-Gaming/silo-xbox/blob/main/PRIVACY.md

## Playing

- **Menu** or **View** on the controller (**T** on a keyboard) opens *Audio, subtitles &
  quality*. A new audio track or quality asks the server for a new stream at the current
  position (a `track_change` or `quality_change` replan); subtitles switch locally between the
  text tracks the server attached.
- **Search** and one button per video library sit in the home header.

## Known gaps

- No watch party, trailers or collections.
- A subtitle switched on its own (without an audio or quality change) is not sent to the
  server, so the next title starts with the server's default again. Protocol v3 has no replan
  operation that only records a sidecar subtitle choice.
- Image-based subtitles (PGS, VobSub) are not offered; they need the server to burn them in.
- ASS/SSA styling (fonts, positions, karaoke) is dropped: those tracks show as plain WebVTT.
- Seeking in a server remux reloads the stream (a second or two), and there is no seek bar to
  drag, only 10 s / 30 s skips.
- Dolby Vision is not available to apps on Xbox; Dolby Vision files play as HDR10 or SDR,
  as the server prepares them. AV1 plays only where the console's hardware decodes it.
- A PIN profile's token is held in memory only, so a cold start returns to the profile picker.
- Tested on an Xbox in Developer Mode against a local Docker Silo server and a remote one.

## License

MIT; see [LICENSE](LICENSE).
