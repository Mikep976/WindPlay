# Security policy

## Reporting a vulnerability

Please do not open a public issue for an undisclosed vulnerability. Use GitHub's private vulnerability reporting for `Mikep976/WindPlay`. Include the affected commit, reproduction steps, impact, and any relevant packet capture with personal identifiers removed.

## Security posture

- Local/private network access only by default.
- Four-digit receiver passcode enabled by default.
- Stable pairing identity generated independently for every installation.
- Private identity material protected with Windows DPAPI for the current user.
- No administrator requirement, service installation, cloud endpoint, telemetry, recording, or automatic upload.
- Bounded RTSP and mirroring payload parsing with fail-closed malformed-input handling.
- Exact dependency versions locked and an ARM64 CI build required.

The four-digit passcode protects against casual or accidental access on a trusted LAN; it is not a substitute for a trusted Wi-Fi password or network segmentation. Do not enable routed/public address access on an untrusted network.

## Scope

The first private test builds are unsigned. Validate that artifacts came from this repository before bypassing SmartScreen. Code signing and MSIX distribution are release gates before public distribution.
