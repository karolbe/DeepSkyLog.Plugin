using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("DeepSkyLog.Plugin.Tests")]

// [MANDATORY] The following GUID is used as a unique identifier of the plugin. Generate a fresh one for your plugin!
[assembly: Guid("728cc59b-a4a0-45cf-89ed-ecd2aa437dd9")]

// [MANDATORY] The assembly versioning
// For local builds, update manually. For CI builds, this is replaced automatically.
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// [MANDATORY] The name of your plugin
[assembly: AssemblyTitle("DeepSkyLog")]
// [MANDATORY] A short description of your plugin
[assembly: AssemblyDescription("Automatically sync your astrophotography session data with DeepSkyLog")]

// The following attributes are not required for the plugin per se, but are required by the official manifest meta data

// Your name
[assembly: AssemblyCompany("Karol Bryd")]
// The product name that this plugin is part of
[assembly: AssemblyProduct("DeepSkyLog")]
[assembly: AssemblyCopyright("Copyright © 2026 Karol Bryd")]

// The minimum Version of N.I.N.A. that this plugin is compatible with.
// Raised to 3.2 for live session telemetry: IFocuserConsumer.NewAutoFocusPoint arrived in 3.2, and
// ISequenceMediator's SequenceStarting/SequenceFinished events in 3.1.
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]

// The license your plugin code is using
[assembly: AssemblyMetadata("License", "MPL-2.0")]
// The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
// The repository where your pluggin is hosted
[assembly: AssemblyMetadata("Repository", "https://github.com/karolbe/DeepSkyLog.Plugin")]

// The following attributes are optional for the official manifest meta data

//[Optional] Your plugin homepage URL - omit if not applicaple
[assembly: AssemblyMetadata("Homepage", "https://deepskylog.space")]

//[Optional] Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "astrophotography,project,management,log,logging,tracking,deepskylog,imaging,session")]

//[Optional] A link that will show a log of all changes in between your plugin's versions
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/karolbe/DeepSkyLog.Plugin/blob/master/DeepSkyLog/CHANGELOG.md")]

//[Optional] The url to a featured logo that will be displayed in the plugin list next to the name
[assembly: AssemblyMetadata("FeaturedImageURL", "")]
//[Optional] A url to an example screenshot of your plugin in action
[assembly: AssemblyMetadata("ScreenshotURL", "https://deepskylog.space/images/illustrations/dashboard.jpg")]
//[Optional] An additional url to an example example screenshot of your plugin in action
[assembly: AssemblyMetadata("AltScreenshotURL", "https://deepskylog.space/images/illustrations/dashboard.jpg")]
//[Optional] An in-depth description of your plugin
[assembly: AssemblyMetadata("LongDescription", @"DeepSkyLog turns your N.I.N.A. captures into an organized, living record of every project - so you always know what you've shot, how good it is, and what to image next.

As each LIGHT frame is saved, the plugin quietly syncs its metadata to the DeepSkyLog web app giving you an up-to-date view of all your imaging in one place.

As an owner of a remote observatory, astrophotography traveller I know how hard it is to keep track of all the data you collect night after night.
I created DeepSkyLog to make it easy to manage my all projects, track progress, and plan my future sessions. I hope it will help you too.

Here is how DeepSkyLog can help you:

• All your targets are organized as Projects, each Project has equipment, location and keeps details about all FITS files captured for it
• See total integration time accumulate night after night, assign goals, and track your progress toward them
• Monitor image quality over time - HFR, FWHM, guiding RMS, eccentricity
• Spot your best and worst frames, especially if you do not own expensive rig :-)

You can also plan future sessions with DeepSkyLog:

• Know exactly which filters still need more data to finish a target
• Plan upcoming nights around what each project still needs
• Use interactive sky map to find interesting targets to image and see what you have already photographed
• Preview mosaic panels (as well as all your targets) on an interactive sky map

Explore a live demo account - no signup: https://deepskylog.space/demo-login

Free version available, or upgrade to Pro for more features:

Learn more at https://deepskylog.space")]
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