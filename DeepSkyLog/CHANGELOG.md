# DeepSkyLog NINA Plugin - Changelog

## 1.0.1.3

### Fixed
- Frames saved to a folder whose name contains a `+` sign (e.g. a target named `M56+92`) were silently discarded — the path was misread and the checksum could not be calculated. Fixed.
- Frames saved to a UNC network share (`\\server\share\...`) failed the same way — the server hostname was stripped from the path. Fixed.
- If the image file is temporarily unreadable at the moment of upload, a fallback checksum is now sent instead of nothing, so the frame is no longer lost.
