# DeepSkyLog NINA Plugin - Changelog

## Unreleased

### Added
- Uploaded frames now include peak guiding error in RA and Dec, in arcseconds, alongside the
  guiding RMS.

### Fixed
- With the plugin switched off, or between sequences, the NINA log no longer gets a "sent no
  telemetry" warning every 15 minutes. These are your own choices, not faults, so they are noted
  once at Debug instead.
- The NINA log is quieter. Routine messages (plugin loading, telemetry attached, session
  started/finished, sign-in steps, frames skipped by type, checksum fallback) are now logged at
  Debug. Failures, sign-in results and plan or update notices are unchanged.
- Signing in to a different DeepSkyLog account or server (for example beta instead of
  production) no longer keeps the previous account's location and equipment. They are cleared,
  with a warning and a notification asking you to pick them again. Signing back in to the same
  account keeps your selection.
- Signing in no longer loads locations and equipment twice, so a stale selection is reported
  once. The message now says frames the server refuses are kept on this PC, rather than that
  frames are not being saved.
- The "Login with DeepSkyLog" button now matches NINA's theme. It was drawn as a light-grey
  button with almost invisible text.
- Wording about paid plans, tiers and upgrades now simply refers to a DeepSkyLog subscription,
  in the options page, the plugin description, notifications and the log.
- A frame kept back because nobody is signed in is now also explained in the NINA log, not only
  in a notification, at the same reminder cadence as the notification.
- Guiding RMS now shows on the Live page for rigs where it was always missing. It is now taken
  from the RMS NINA records for each exposure, the same values written into the FITS header,
  instead of the live guider readout, which some setups never fill in. Unguided frames report no
  guiding rather than 0.00".
- A sign-in that expires, or a plugin that is enabled without being signed in, is now reported
  with a notification. Previously frames were quietly kept on disk while the options still said
  "Connected". An expired sign-in is re-checked straight away, so the Login button comes back.
- Being offline when NINA starts no longer reports your saved location and equipment as deleted
  and blanks the dropdowns. The saved selection is kept, and the options say DeepSkyLog could not
  be reached.
- When the account's plan does not include NINA uploads, the plugin now says so plainly and points
  to the plan rather than the location/equipment selection.
- Notifications no longer repeat every minute while a problem persists. Each kind of problem is
  announced when it first appears and reminded at most every 30 minutes. It is announced again
  straight away once it has cleared and comes back. A telemetry refusal can no longer hide a
  rejected-frame notification.

## 1.0.4.0

### Changed
- Calibration frames — flats, darks and biases — are no longer uploaded. They carry the target name
  and coordinates of whichever sequence happened to be loaded, so a morning's flats were filed under
  the previous night's target. A new "Skip Calibration" option, on by default, controls this;
  turn it off to keep the old behaviour.
- Snapshots are no longer uploaded unless you ask for them. The "Allow SNAPSHOTs" option has always
  defaulted to off, but the check that enforced it had been commented out, so snapshots were sent
  regardless. The switch is now honoured.
- The equipment dropdown no longer repeats itself: a named record already reads
  "Esprit 120 + ZWO ASI2600MM-Pro @ 850 mm", so the telescope and camera are no longer appended a
  second time. Records with no name now show their focal length, which is often the only thing
  telling one rig from another.

## 1.0.3.0

### Added
- Live session telemetry: while a sequence runs, the plugin reports the rig's state to DeepSkyLog
  every 10 seconds and shows it on the new Live page in the web app. No images are uploaded.
  - Session start and end, from the sequencer
  - Mount pointing (RA/Dec, alt/az, pier side, tracking, park, time to meridian flip)
  - Safety monitor state and roof/dome shutter transitions
  - Autofocus runs, including the measured HFR and the full V-curve
  - Guiding RMS, camera temperature and cooler power, focuser position and temperature
- New option: how often telemetry is reported (5-300 seconds, 10 by default).
- When the server refuses an upload — usually a location or equipment that was deleted from your
  account — the plugin now says so with a notification and in its options, and offers a button to
  send the kept-back frames once you have fixed the selection.
- The plugin now tells you when a newer version has been published.

### Changed
- Minimum N.I.N.A. version is now 3.1.0.9001.
- Uploads that fail because the server is unreachable are kept and retried more patiently, instead
  of being retried on every saved frame.

### Fixed
- Guiding RMS no longer reports 0.00" when the guider is connected but not actively guiding; it is
  now left blank so "idle" and "guiding perfectly" are no longer indistinguishable.

## 1.0.1.3

### Fixed
- Frames saved to a folder whose name contains a `+` sign (e.g. a target named `M56+92`) were silently discarded — the path was misread and the checksum could not be calculated. Fixed.
- Frames saved to a UNC network share (`\\server\share\...`) failed the same way — the server hostname was stripped from the path. Fixed.
- If the image file is temporarily unreadable at the moment of upload, a fallback checksum is now sent instead of nothing, so the frame is no longer lost.
