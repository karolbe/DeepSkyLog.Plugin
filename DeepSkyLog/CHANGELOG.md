# DeepSkyLog NINA Plugin - Changelog

## Unreleased

### Added
- Live session telemetry: while a sequence runs, the plugin reports the rig's state to DeepSkyLog
  every 10 seconds and shows it on the new Live page in the web app. No images are uploaded.
  - Session start and end, from the sequencer
  - Mount pointing (RA/Dec, alt/az, pier side, tracking, park, time to meridian flip)
  - Safety monitor state and roof/dome shutter transitions
  - Autofocus runs, including the full measured V-curve
  - Guiding RMS, camera temperature and cooler power, focuser position and temperature
- New options: "Live Telemetry" on/off and the reporting interval.

### Changed
- Minimum N.I.N.A. version is now 3.2.0.9001. The telemetry mediators the new features use were
  added across N.I.N.A. 3.1 and 3.2.

## 1.0.1.3

### Fixed
- Frames saved to a folder whose name contains a `+` sign (e.g. a target named `M56+92`) were silently discarded — the path was misread and the checksum could not be calculated. Fixed.
- Frames saved to a UNC network share (`\\server\share\...`) failed the same way — the server hostname was stripped from the path. Fixed.
- If the image file is temporarily unreadable at the moment of upload, a fallback checksum is now sent instead of nothing, so the frame is no longer lost.
