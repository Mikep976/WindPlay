# Hardware acceptance test

RELEASE HOLD: Do not use run 14. The remediation and signing gates must be resolved before these instructions authorize a Surface test.

The automated suite validates protocol framing, malformed-input rejection, identity isolation, authentication, H.264 framing, and an ARM64 build. Real AirPlay interoperability must also be checked on the target Surface because sender behavior, Wi-Fi multicast, GPU drivers, and audio clocks are hardware-dependent.

## Test setup

- Surface Pro running current Windows 11 on ARM updates.
- Mac and Surface on the same trusted, non-guest Wi-Fi network.
- Windows network profile set to **Private**.
- Latest `WindPlay-win-arm64` artifact extracted to a local folder.

Record the Surface model, Windows build, Mac model, macOS version, iPhone model/iOS version, access point, and whether a VPN is active.

## Primary: extended Mac desktop

1. Start WindPlay and confirm **Ready on your local network**.
2. On macOS, open Control Center → Screen Mirroring → WindPlay.
3. Enter the 20-character WindPlay receiver password if prompted. Rejecting this password length is an interoperability failure, not permission to silently downgrade to four digits.
4. Open System Settings → Displays, select WindPlay, and choose **Use as Separate Display**.
5. Move a window and the pointer across the display boundary.
6. Play a 60 fps non-DRM video and a voice track for five minutes.
7. Rotate neither device; resize a source window and switch macOS display scaling once.
8. Disconnect from the WindPlay overlay, then reconnect.

Pass criteria:

- The Surface is offered by macOS and extension mode can be selected.
- First useful frame appears within five seconds after pairing.
- Motion remains responsive, with no accumulating delay over five minutes.
- Audio is intelligible and does not steadily drift from video.
- Disconnect/reconnect requires no app restart.

## Mirroring and audio

- Mirror an iPhone for five minutes, including portrait/landscape rotation.
- Mirror the Mac rather than extend it.
- Send audio-only playback and verify play/pause and receiver volume.
- Lock the iPhone or sleep the Mac and verify the stream ends cleanly.

## Privacy and hostile-LAN checks

- Reject an incorrect passcode.
- Turn the receiver off and verify it disappears from the AirPlay picker.
- Leave **Allow routed or public IP addresses** off.
- Confirm `%LOCALAPPDATA%\WindPlay` contains no recording or screenshot files.
- Confirm no log files are created unless diagnostics are explicitly enabled.
- If diagnostics are enabled, disable them again and verify the receiver restarts.

## Report template

```text
Surface / Windows:
Mac / macOS:
iPhone / iOS:
Network / VPN:
Discovery: pass/fail
Passcode prompt: pass/fail/not shown
Extend display: pass/fail
First-frame time:
Audio: pass/fail/not tested
Reconnect: pass/fail
Observed delay or stutter:
Anything visible in the WindPlay error bar:
```
