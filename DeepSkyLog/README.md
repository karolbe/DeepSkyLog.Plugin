# DeepSkyLog NINA Plugin

Automatically sync your astrophotography session data with [DeepSkyLog](https://deepskylog.space).

## Features

- **One-click login** - Browser-based authentication with your DeepSkyLog account
- **Automatic sync** - LIGHT frame metadata is uploaded as images are saved during your sequence
- **Location & equipment selection** - Choose your observing site and equipment setup from your DeepSkyLog profile
- **Comprehensive tracking** - Monitor image quality, guiding performance, and environmental conditions

## Installation

1. Download the plugin from the NINA plugin manager or from [GitHub Releases](https://github.com/karolbe/deepskylog-nina-plugin/releases)
2. Install the plugin in NINA
3. Go to Options > DeepSkyLog
4. Click "Login with DeepSkyLog" to authenticate
5. Select your location and equipment
6. Enable the plugin

## Data Synchronized

The following metadata is sent to DeepSkyLog when LIGHT frames are saved:

### Image Details
- File path, filter, exposure time, binning, gain, offset, and camera temperature

### Quality Metrics
- Mean and median ADU, detected stars, HFR, FWHM, and eccentricity

### Guiding & Focusing
- Guiding RMS (total, RA, Dec), focuser position, rotator angle, and pier side

### Weather Conditions
- Temperature, humidity, dew point, wind speed, cloud cover, and sky quality (SQM)

### Target & Equipment
- Target name, RA/Dec coordinates, telescope, camera, focal length, and pixel size

### Observation Info
- Airmass, altitude, azimuth, moon phase, and observer location

## Requirements

- N.I.N.A. 3.0.0.2017 or later
- A DeepSkyLog account at [deepskylog.space](https://deepskylog.space)

## License

This plugin is licensed under the [Mozilla Public License 2.0](https://www.mozilla.org/en-US/MPL/2.0/).

## Support

- [GitHub Issues](https://github.com/karolbe/deepskylog-nina-plugin/issues)
- [DeepSkyLog Website](https://deepskylog.space)
