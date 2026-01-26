using System.Reflection;
using System.Runtime.InteropServices;

// [MANDATORY] The following GUID is used as a unique identifier of the plugin. Generate a fresh one for your plugin!
[assembly: Guid("728cc59b-a4a0-45cf-89ed-ecd2aa437dd9")]

// [MANDATORY] The assembly versioning
//Should be incremented for each new release build of a plugin
[assembly: AssemblyVersion("1.0.0.1")]
[assembly: AssemblyFileVersion("1.0.0.1")]

// [MANDATORY] The name of your plugin
[assembly: AssemblyTitle("DeepSkyLog")]
// [MANDATORY] A short description of your plugin
[assembly: AssemblyDescription("Automatically sync your astrophotography session data with DeepSkyLog")]

// The following attributes are not required for the plugin per se, but are required by the official manifest meta data

// Your name
[assembly: AssemblyCompany("Karol Bryd")]
// The product name that this plugin is part of
[assembly: AssemblyProduct("DeepSkyLog")]
[assembly: AssemblyCopyright("Copyright © 2025 Karol Bryd")]

// The minimum Version of N.I.N.A. that this plugin is compatible with
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.0.0.2017")]

// The license your plugin code is using
[assembly: AssemblyMetadata("License", "MPL-2.0")]
// The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
// The repository where your pluggin is hosted
[assembly: AssemblyMetadata("Repository", "https://github.com/karolbe/nina.plugin.deepskylog")]

// The following attributes are optional for the official manifest meta data

//[Optional] Your plugin homepage URL - omit if not applicaple
[assembly: AssemblyMetadata("Homepage", "https://deepskylog.space")]

//[Optional] Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "astrophotography, logging, tracking, deepskylog, imaging, session")]

//[Optional] A link that will show a log of all changes in between your plugin's versions
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/karolbe/nina.plugin.deepskylog/blob/main/CHANGELOG.md")]

//[Optional] The url to a featured logo that will be displayed in the plugin list next to the name
[assembly: AssemblyMetadata("FeaturedImageURL", "")]
//[Optional] A url to an example screenshot of your plugin in action
[assembly: AssemblyMetadata("ScreenshotURL", "")]
//[Optional] An additional url to an example example screenshot of your plugin in action
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
//[Optional] An in-depth description of your plugin
[assembly: AssemblyMetadata("LongDescription", @"DeepSkyLog automatically syncs your astrophotography session data with the DeepSkyLog web application.

Features:
- One-click browser-based login with your DeepSkyLog account
- Automatic upload of LIGHT frame metadata as images are saved
- Select your observing location and equipment setup
- Track image quality metrics, guiding performance, and weather conditions

Data synchronized includes target coordinates, exposure settings, quality metrics (HFR, FWHM, stars detected), guiding RMS, focuser position, weather data, and more.")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]
// [Unused]
[assembly: AssemblyConfiguration("")]
// [Unused]
[assembly: AssemblyTrademark("")]
// [Unused]
[assembly: AssemblyCulture("")]