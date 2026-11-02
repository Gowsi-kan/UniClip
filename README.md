# UniClip

UniClip is a .NET 10 Avalonia app for moving clipboard text and files between
desktop devices. Run the app on a Windows or Linux laptop to pair with other
instances.

## Run the desktop app

```sh
dotnet run --project UniClip.Desktop
```

The default solution builds the shared app and desktop host without platform
workloads:

```sh
dotnet build
```

The browser front end remains an optional project outside the default solution
and requires the WebAssembly .NET workload to build. Install it once, then run
the preview with:

```sh
dotnet workload install wasm-tools
dotnet run --project UniClip.Browser
```

The browser preview does not support device pairing or file and clipboard
transfers.

## Current status

The app can create a 10-minute QR pairing invite, start a libp2p listener,
advertise it over mDNS, and connect to a pasted invite. The libp2p stack
authenticates peers with its Noise handshake (X25519) before reporting a
connection. Files are streamed in bounded 64 KiB chunks over that encrypted
session, checked with SHA-256, and saved without overwriting existing files.
The receiver must approve each incoming file.

Invites contain reachable local addresses and a peer identity. The receiver
can scan the QR with a QR reader and paste its payload into the invite field.
Device identities are currently in-memory only, so devices must pair again
after an app restart.

Clipboard sync is available for text when both paired devices explicitly enable
it. While the app window is active, it checks the system clipboard every 800 ms
and sends only changed text over the authenticated encrypted session. The
current clipboard value is used as a baseline when sync starts, preventing
existing contents from being shared automatically. Incoming text is applied
only when sync is enabled locally. Text is limited to 256 KiB and kept in
memory; clipboard history is not stored. Clipboard contents may be sensitive,
so disable sync when it is not wanted. Relay fallback is not wired up, and the
in-memory peer identity is regenerated after an app restart. This prototype
has not received an independent security review.

Received files are stored in `~/Downloads` when that folder exists; otherwise
they go to the app's local application-data folder under `UniClip/Received`.

The .NET libp2p package supports Noise and mDNS. The desktop target is the
supported way to run UniClip on a Windows or Linux laptop.

## Project layout

- `UniClip/` - shared Avalonia UI and view models.
- `UniClip.Desktop/` - Windows, Linux, and macOS desktop host.
- `UniClip.Browser/` - optional WebAssembly preview host.
