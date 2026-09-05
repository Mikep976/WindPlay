# Security policy

## Reporting a vulnerability

Please do not open a public issue for an undisclosed vulnerability. Use GitHub's private vulnerability reporting for `Mikep976/WindPlay`. Include the affected commit, reproduction steps, impact, and any relevant packet capture with personal identifiers removed.

## Security posture

- Selected IPv4 Ethernet/Wi-Fi subnet only. Routed/public access is disabled.
- Random 20-character receiver password enabled by default (100-bit base32 alphabet).
- Stable pairing identity generated independently for every installation.
- Private identity material protected with Windows DPAPI for the current user.
- No administrator requirement, service installation, cloud endpoint, telemetry, recording, or automatic upload.
- Bounded binary plist, DMap, iterative DNS, RTSP and mirroring parsing with pre-allocation limits and session deadlines.
- Exact dependency versions locked and an ARM64 CI build required.

Legacy AirPlay Digest permits offline verification of captured guesses; online limits cannot prevent that. WindPlay no longer relies on a four-digit secret. Existing PINs migrate to a random 100-bit password, and the UI can rotate it while closing current sessions. The password is not copied to the clipboard. It remains no substitute for trusted Wi-Fi, segmentation, and a firewall rule scoped to the sender IP/LocalSubnet. Longer-password interoperability remains a hardware acceptance gate.

Run 14 is withdrawn from testing. Do not use that unsigned artifact. The current source remediation does not authorize installation: see `docs/SECURITY-REMEDIATION.md` and the fail-closed gates in `eng/release-gates.json`.

## Scope

The first private test builds are unsigned. Validate that artifacts came from this repository before bypassing SmartScreen. Code signing and MSIX distribution are release gates before public distribution.
