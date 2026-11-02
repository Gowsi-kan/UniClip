<p align="center">
  <img src="UniClip/Assets/Icon.png" alt="UniClip app icon" width="112" />
</p>

<h1 align="center">UniClip</h1>

<p align="center">
  Share clipboard text and files directly between paired devices on your local network.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/C%23-Programming%20Language-512BD4?style=for-the-badge&logo=csharp&logoColor=white" alt="C sharp" />
  <img src="https://img.shields.io/badge/Avalonia-UI%20Framework-8B5CF6?style=for-the-badge" alt="Avalonia UI" />
  <img src="https://img.shields.io/badge/libp2p-Peer%20Networking-3B6CFF?style=for-the-badge" alt="libp2p" />
</p>

UniClip is a .NET 10 desktop app built with Avalonia. Pair two devices with a
temporary six-digit PIN, then send files or opt in to clipboard text sync.
Connections are local-network-first; UniClip does not currently provide relay
or internet-based connectivity.

> **Prototype notice:** UniClip has not received an independent security
> review. Read [Security and privacy](#security-and-privacy) before using it
> with sensitive data.

## Features

- Pair devices using a single-use PIN that expires after 10 minutes.
- Discover peers on the local network and connect directly.
- Send files over an authenticated encrypted session. The recipient must
  approve each incoming file; transfers are streamed and verified with
  SHA-256.
- Optionally sync clipboard text after enabling sync on both paired devices.
- Keep clipboard text in memory only; text is limited to 256 KiB and no
  clipboard history is stored.
- Save received files in `~/Downloads` when available, or in the app's local
  application-data directory otherwise.

## Requirements

- .NET 10 SDK
- Windows or Linux for the desktop app
- Both devices connected to the same local network for pairing and direct
  transfers

## Get started

Clone the repository and launch the desktop app:

```sh
git clone https://github.com/Gowsi-kan/UniClip.git
cd UniClip
dotnet run --project UniClip.Desktop
```

To build the default solution:

```sh
dotnet build
```

In the app, share the displayed PIN with the other device and enter that
device's PIN. PINs are single-use and expire after 10 minutes. If a connection
fails after a PIN is accepted, generate a new PIN before trying again.

## Network and firewall setup

The desktop app starts its rendezvous PIN service and attempts to configure
the required inbound firewall rules. On Windows, it requests administrator
approval only when the rules need to be added or updated; the app itself does
not run as administrator. On Linux, it requests approval to add rules when
UFW or firewalld is active. If automatic setup is unavailable or declined,
configure the same rules manually:

| Port | Protocol | Purpose |
| --- | --- | --- |
| 5180 | TCP | Exchange temporary pairing PINs and peer invites |
| 5181 | UDP | Discover devices on the local network |
| 5182 | TCP | Direct peer connection |

Rules are restricted to local or private network traffic where supported.
Router client isolation or network firewall policies may still prevent
devices from discovering or reaching each other. Only share a PIN with a
device you trust, especially on public Wi-Fi.

### Run the rendezvous service separately

The desktop app starts this service automatically. To run it separately for
development or testing:

```sh
dotnet run --project UniClip.Rendezvous -- --urls http://0.0.0.0:5180
```

The service stores active pairing entries in memory, rate-limits requests per
client IP, and discards active PINs when it restarts. Its HTTP pairing request
contains the peer invite. Do not expose it beyond a trusted network without a
properly configured HTTPS reverse proxy. Running a separate rendezvous service
does not add relay or cross-network connectivity.

## Security and privacy

- The six-digit PIN is temporary, single-use, and valid for 10 minutes. Treat
  it like a password: anyone who claims it can retrieve the associated peer
  invite.
- Peer connections use the libp2p Noise handshake (X25519) before the app
  reports a connection.
- Clipboard sync is opt-in on each device. When enabled, the active app checks
  for changed clipboard text every 800 ms. The current clipboard contents are
  used as a baseline when sync starts, and incoming text is applied only when
  sync is enabled locally.
- Clipboard text is limited to 256 KiB, kept in memory, and not saved as
  history. Disable sync when clipboard contents should not be shared.
- Pairing invites contain reachable local addresses and a peer identity.
  Device identities are currently held in memory, so devices must pair again
  after an app restart.
- Temporary diagnostic logs are written under `logs/` beside the application
  executable. Pairing PINs, invites, clipboard text, and file contents are
  redacted or not recorded. Logs can contain network and peer addresses, so
  review them before sharing. To disable logging after troubleshooting, set
  `SessionDiagnostics.Enabled` to `false` in
  [`SessionDiagnostics.cs`](UniClip/Services/SessionDiagnostics.cs).

## Browser preview

The browser project is an optional UI preview. It does not support device
pairing, clipboard synchronization, or file transfers. Install the WebAssembly
workload and run it with:

```sh
dotnet workload install wasm-tools
dotnet run --project UniClip.Browser
```

## Project structure

| Project | Description |
| --- | --- |
| [`UniClip/`](UniClip/) | Shared Avalonia UI, view models, and transfer/pairing logic |
| [`UniClip.Desktop/`](UniClip.Desktop/) | Desktop application host |
| [`UniClip.Rendezvous/`](UniClip.Rendezvous/) | In-memory API for exchanging temporary pairing invites |
| [`UniClip.Browser/`](UniClip.Browser/) | Optional WebAssembly UI preview |

## Current limitations

- Pairing and direct transfers require devices to be reachable on the same
  local network; relay support is not implemented.
- Peer identities are not persisted across app restarts.
- Clipboard synchronization supports text only.
- This is a prototype and has not received an independent security review.
