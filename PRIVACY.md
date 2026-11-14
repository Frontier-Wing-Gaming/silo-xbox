# Silolux for Xbox: Privacy Policy

Effective 13 November 2026.

Silolux for Xbox ("the app") is an independent client for Silo, a media server you or someone
you know runs. It is published by Frontier Wing Gaming and is not affiliated with the Silo
project.

## The short version

The app talks to one place: the Silo server whose address you enter. Frontier Wing Gaming runs
no servers for the app and receives nothing from it: no analytics, no advertising, no tracking,
no crash reports.

## What the app sends to your Silo server

To sign you in, show your library and play it, the app sends your Silo server:

- Your Silo username and password if you sign in with them, or a sign-in code if you approve
  the app from another device. The password is sent once, to sign in, and is not stored.
- Which profile is watching, and its PIN if the profile has one.
- What you browse, search for and play, and how far you got, so the server can keep your
  place and show Continue Watching.
- A device identifier the app makes up at random on first run, the device name "Xbox", the app
  name and version, the Windows version, and which video and audio formats the console can play.
  The server uses these to list the device among your signed-in devices and to choose a stream
  the console can play.

What your server does with this is up to whoever runs it. Silo's own documentation and that
person's policies apply there, not this one.

## What the app keeps on the console

- The server address, the random device identifier, and which profile you picked, in the app's
  own settings.
- A sign-in token, in the Windows Credential Locker, so you stay signed in. A profile's PIN
  token is kept in memory only and is gone when the app closes.
- If the app crashes, a crash log with technical details of the error (`crash.log` in the
  app's local storage). It stays on the console; the app never sends it anywhere.

Signing out removes the sign-in token and the profile choice. Uninstalling the app removes
everything above.

## Images and video

Posters, artwork, video and subtitles come from your Silo server, or from a streaming address
that server hands the app. Your sign-in token is only ever attached to requests to your own
server's address.

## Microsoft

The app is installed and run by Windows on Xbox and the Microsoft Store. Microsoft's own
privacy statement covers what they collect, such as Store downloads and system diagnostics:
https://privacy.microsoft.com/privacystatement. The app adds nothing to it.

## Children

The app has no accounts of its own and collects nothing for Frontier Wing Gaming. Profile
restrictions, such as age limits on a child's profile, are set on the Silo server.

## Changes and contact

Changes to this policy are published in this file, and its history is on GitHub. Questions:
open an issue at https://github.com/Frontier-Wing-Gaming/silo-xbox/issues.
