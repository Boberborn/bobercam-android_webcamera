#if ANDROID
using System.Buffers;
using System.Runtime.InteropServices;
using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Android.OS;
using Java.IO;

namespace BobrCam;

internal static class AndroidUsbAccessoryTransport
{
    private const string PermissionAction =
        "com.bobrcam.app.USB_ACCESSORY_PERMISSION";

    public static async Task<Stream?> OpenAttachedAsync(
        CancellationToken cancellationToken)
    {
        var context = Platform.AppContext;
        var manager = context.GetSystemService(Context.UsbService) as UsbManager;
        var accessory = manager?.GetAccessoryList()?.FirstOrDefault();
        if (manager is null || accessory is null)
            return null;

        if (!manager.HasPermission(accessory) &&
            !await RequestPermissionAsync(
                context,
                manager,
                accessory,
                cancellationToken))
        {
            throw new UnauthorizedAccessException(
                "USB permission was denied. Reconnect the cable and select BobrCam.");
        }

        var descriptor = manager.OpenAccessory(accessory) ??
            throw new System.IO.IOException(
                "Windows opened USB accessory mode, but Android could not open it.");
        return new AndroidUsbAccessoryStream(descriptor);
    }

    private static async Task<bool> RequestPermissionAsync(
        Context context,
        UsbManager manager,
        UsbAccessory accessory,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var receiver = new PermissionReceiver(completion);
        using var filter = new IntentFilter(PermissionAction);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        else
        {
#pragma warning disable CA1422
            context.RegisterReceiver(receiver, filter);
#pragma warning restore CA1422
        }
        try
        {
            using var intent = new Intent(PermissionAction)
                .SetPackage(context.PackageName);
            var flags = PendingIntentFlags.UpdateCurrent;
            if (OperatingSystem.IsAndroidVersionAtLeast(23))
                flags |= PendingIntentFlags.Immutable;
            using var pendingIntent = PendingIntent.GetBroadcast(
                context,
                0,
                intent,
                flags);
            manager.RequestPermission(accessory, pendingIntent);
            return await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(30),
                cancellationToken);
        }
        finally
        {
            try { context.UnregisterReceiver(receiver); }
            catch (ArgumentException) { }
        }
    }

    private sealed class PermissionReceiver(
        TaskCompletionSource<bool> completion) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action != PermissionAction)
                return;
            completion.TrySetResult(
                intent.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false));
        }
    }

    private sealed class AndroidUsbAccessoryStream : Stream
    {
        private readonly ParcelFileDescriptor _inputDescriptor;
        private readonly ParcelFileDescriptor _outputDescriptor;
        private readonly FileInputStream _input;
        private readonly FileOutputStream _output;
        private bool _disposed;

        public AndroidUsbAccessoryStream(ParcelFileDescriptor descriptor)
        {
            try
            {
                var fileDescriptor = descriptor.FileDescriptor ??
                    throw new System.IO.IOException("USB accessory descriptor is invalid.");
                _inputDescriptor = ParcelFileDescriptor.Dup(fileDescriptor) ??
                    throw new System.IO.IOException("Could not duplicate USB input descriptor.");
                _outputDescriptor = ParcelFileDescriptor.Dup(fileDescriptor) ??
                    throw new System.IO.IOException("Could not duplicate USB output descriptor.");
                _input = new FileInputStream(_inputDescriptor.FileDescriptor);
                _output = new FileOutputStream(_outputDescriptor.FileDescriptor);
            }
            finally
            {
                descriptor.Dispose();
            }
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _input.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                var read = _input.Read(rented, 0, buffer.Length);
                if (read > 0)
                    rented.AsSpan(0, read).CopyTo(buffer.Span);
                return ValueTask.FromResult(read);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _output.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (MemoryMarshal.TryGetArray(buffer, out var segment) &&
                segment.Array is not null)
            {
                _output.Write(segment.Array, segment.Offset, segment.Count);
                return ValueTask.CompletedTask;
            }

            var copy = buffer.ToArray();
            _output.Write(copy, 0, copy.Length);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _output.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Flush();
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                try { _input.Dispose(); } catch { }
                try { _output.Dispose(); } catch { }
                try { _inputDescriptor.Dispose(); } catch { }
                try { _outputDescriptor.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();
    }
}
#endif
