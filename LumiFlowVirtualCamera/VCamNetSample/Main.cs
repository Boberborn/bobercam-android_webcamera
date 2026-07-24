using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Forms;
using DirectN;
using VCamNetSampleSource;

namespace VCamNetSample
{
    public partial class Main : Form
    {
        private IComObject<IMFVirtualCamera>? _camera;
        private bool _mediaFoundationStarted;

        public Main()
        {
            InitializeComponent();
            Icon = Resources.MainIcon;
            Text = AssemblyUtilities.GetTitle(Assembly.GetExecutingAssembly());
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Hide();
            ShowInTaskbar = false;

            var arguments = Environment.GetCommandLineArgs();
            var remove = Array.Exists(arguments, argument =>
                string.Equals(argument, "--remove", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "--uninstall", StringComparison.OrdinalIgnoreCase));
            var quiet = Array.Exists(arguments, argument =>
                string.Equals(argument, "--quiet", StringComparison.OrdinalIgnoreCase));

            var td = new DirectN.TaskDialog
            {
                Title = Text,
                CommonButtonFlags = TASKDIALOG_COMMON_BUTTON_FLAGS.TDCBF_CLOSE_BUTTON
            };

            MFFunctions.MFStartup();
            _mediaFoundationStarted = true;
            var hr = Functions.MFCreateVirtualCamera(
                __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0001.MFVirtualCameraType_SoftwareCameraSource,
                __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0002.MFVirtualCameraLifetime_System,
                __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0003.MFVirtualCameraAccess_CurrentUser,
                Text,
                "{" + Shared.CLSID_VCamNet + "}",
                null,
                0,
                out var camera);
            if (hr.IsSuccess)
            {
                _camera = new ComObject<IMFVirtualCamera>(camera);
                hr = remove
                    ? _camera.Object.Remove()
                    : _camera.Object.Start(null);
            }

            if (hr.IsError)
            {
                td.MainInstruction = remove
                    ? "BobrCam virtual camera could not be removed."
                    : "BobrCam virtual camera could not be installed. Make sure the BobrCam camera source is registered.";
                td.Content = $"Error {hr} {hr.Value} {new Win32Exception(hr.Value).Message}";
                td.MainIcon = DirectN.TaskDialog.TD_ERROR_ICON;
            }
            else
            {
                td.MainInstruction = remove
                    ? "BobrCam was removed from the Windows camera list."
                    : "BobrCam is installed as a Windows camera for Zoom, OBS, Teams, browsers, and Camera.";
                td.Content = remove
                    ? "The camera can be restored by running the BobrCam camera installer."
                    : "It remains registered across sign-ins and restarts until BobrCam is uninstalled.";
                td.MainIcon = DirectN.TaskDialog.TD_INFORMATION_ICON;
            }

            Environment.ExitCode = hr.IsError ? hr.Value : 0;
            if (!quiet)
                td.Show(Handle);

            ShutdownMediaFoundation();
            Close();
        }

        private void ShutdownMediaFoundation()
        {
            if (!_mediaFoundationStarted)
                return;

            MFFunctions.MFShutdown();
            _mediaFoundationStarted = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null)
            {
                ShutdownMediaFoundation();
                components?.Dispose();
                _camera?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
