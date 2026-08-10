using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

#if DEBUG
[assembly: AssemblyConfiguration("DEBUG")]
#else
[assembly: AssemblyConfiguration("RELEASE")]
#endif
[assembly: AssemblyTitle("BobrCam Virtual Camera Source")]
[assembly: AssemblyDescription("BobrCam Media Foundation virtual camera source")]
[assembly: AssemblyCompany("BobrCam")]
[assembly: AssemblyProduct("BobrCam - Free Webcam for OBS & PC")]
[assembly: AssemblyCopyright("BobrCam; based on the MIT-licensed VCamNetSample by Simon Mourier")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: Guid("c4997325-bb47-4d8f-8774-f949026afc68")]
[assembly: SupportedOSPlatform("windows10.0.22621.0")]
