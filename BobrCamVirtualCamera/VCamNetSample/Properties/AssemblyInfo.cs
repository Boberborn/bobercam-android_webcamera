using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

#if DEBUG
[assembly: AssemblyConfiguration("DEBUG")]
#else
[assembly: AssemblyConfiguration("RELEASE")]
#endif
[assembly: AssemblyTitle("BobrCam")]
[assembly: AssemblyDescription("BobrCam persistent Windows virtual camera registrar")]
[assembly: AssemblyCompany("BobrCam")]
[assembly: AssemblyProduct("BobrCam - Free Webcam for OBS & PC")]
[assembly: AssemblyCopyright("Copyright (C) 2026 BobrCam. Virtual-camera foundation used under its included license.")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: Guid("f5720613-b59e-4228-b67e-b71727dc7fba")]
[assembly: SupportedOSPlatform("windows10.0.22621.0")]
