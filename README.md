# UniClip

UniClip is a .NET 10 Avalonia app for moving clipboard text and files between
desktop devices. Run the app on a Windows or Linux laptop to pair with other
instances.

## Run the desktop app

```sh
dotnet run --project UniClip.Desktop
```

## Set up PIN pairing

Each desktop app starts a temporary PIN server and displays its own six-digit
PIN on launch. Share either device's PIN, enter it in the six-digit OTP input
on the other device, and select **Pair using PIN**. Both PCs must be on the
same local network. The apps discover each other automatically; no server URL
or separate service setup is needed.

Allow UniClip through the private-network firewall on both PCs. PIN exchange
uses TCP port 5180 and LAN discovery uses UDP port 5181; the direct peer
connection also needs the app's peer-to-peer listener allowed. Use a regular
private Wi-Fi network, not a guest network, and ensure the router does not have
wireless/client isolation enabled. The PIN expires after ten minutes and can
be claimed only once. Generate a new PIN after an unsuccessful connection.

If pairing fails, the status message identifies whether UniClip could not find
or reach a PIN server, or whether the PIN was claimed but the direct device
connection failed. A connection failure after the PIN is accepted consumes
that PIN, so create a new one before retrying.

To run the rendezvous service separately:

```sh
dotnet run --project UniClip.Rendezvous -- --urls http://0.0.0.0:5180
```

Treat the PIN like a temporary password: anyone who claims it can retrieve the
invite, so share it only with the device you intend to pair.

The service keeps pairing entries in memory, rate-limits requests per client
IP, and loses active PINs when it restarts. Keep it on a trusted local network:
the HTTP rendezvous request contains the peer invite. Use a properly configured
HTTPS reverse proxy if exposing the service beyond a trusted LAN. This does
not add cross-network connectivity; relay support is not implemented. The host
device may also need to allow UniClip's peer-to-peer listener through its
private-network firewall.

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

The app can create a temporary six-digit PIN through the self-hosted
rendezvous service, start a libp2p listener, advertise it over mDNS, and claim
the PIN on another device to retrieve the peer invite. The invite is passed
between apps automatically and is not shown to the user. The PIN is single-use
and expires after ten minutes. The libp2p stack authenticates peers with its
Noise handshake (X25519) before reporting a connection. Files are streamed in
bounded 64 KiB chunks over that encrypted session, checked with SHA-256, and
saved without overwriting existing files. The receiver must approve each
incoming file.

Invites contain reachable local addresses and a peer identity. PIN pairing
therefore requires both devices to be reachable on the same local network.
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
- `UniClip.Rendezvous/` - self-hosted API that exchanges temporary pairing invites by PIN.
- `UniClip.Browser/` - optional WebAssembly preview host.
