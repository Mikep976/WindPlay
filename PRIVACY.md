# Privacy

WindPlay processes AirPlay streams locally and ephemerally.

- No account or cloud relay.
- No analytics, advertising identifier, or telemetry SDK.
- No screen recording, screenshots, media library, or playback history.
- No crash or diagnostic upload.
- Diagnostic file logging is opt-in, local, size-limited, and retained for seven days.
- Receiver identity and passcode are stored under the current Windows user profile; secret values are encrypted by Windows DPAPI.

Network endpoint details can appear in optional diagnostic logs because they are necessary to troubleshoot discovery and transport. Do not share logs without reviewing them. Deleting `%LOCALAPPDATA%\WindPlay` removes settings, logs, and the receiver identity; Apple devices will then treat WindPlay as a new receiver.
