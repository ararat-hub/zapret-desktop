using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ZapretDesktop;

internal sealed class LegacyZapretService : IDisposable
{
    private const string Name = "zapret";
    private const uint ScManagerConnect = 0x0001;
    private const uint QueryStatus = 0x0004;
    private const uint StartAccess = 0x0010;
    private const uint StopAccess = 0x0020;
    private const uint StopControl = 1;
    private const int DoesNotExist = 1060;
    private const int NotActive = 1062;
    private const int AlreadyRunning = 1056;
    private const int DisabledStartType = 4;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly ServiceHandle service;
    private bool restoreRequired;

    private LegacyZapretService(ServiceHandle service) => this.service = service;

    public static LegacyZapretService? Open()
    {
        using var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        var service = OpenService(manager, Name, QueryStatus | StartAccess | StopAccess);
        if (!service.IsInvalid)
            return new LegacyZapretService(service);

        int error = Marshal.GetLastPInvokeError();
        service.Dispose();
        if (error == DoesNotExist)
            return null;
        throw ServiceError("Открыть", error);
    }

    public bool StopIfRunning(CancellationToken token)
    {
        var state = WaitUntilStable(token);
        if (state != ServiceState.Running)
            return false;

        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Name))
        {
            if (key?.GetValue("Start") is DisabledStartType)
                throw new InvalidOperationException("У работающей службы zapret отключён запуск. Windows не сможет запустить её снова.");
        }

        token.ThrowIfCancellationRequested();
        if (!ControlService(service, StopControl, out _))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == NotActive)
                return false;
            throw ServiceError("Остановить", error);
        }

        // Once Windows accepts STOP, finish the transition before acting on cancellation.
        restoreRequired = true;
        WaitForState(ServiceState.Stopped, CancellationToken.None);
        token.ThrowIfCancellationRequested();
        return true;
    }

    public bool Restore()
    {
        if (!restoreRequired)
            return false;

        var state = WaitUntilStable(CancellationToken.None);
        if (state == ServiceState.Stopped)
        {
            if (!StartService(service, 0, IntPtr.Zero))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != AlreadyRunning)
                    throw ServiceError("Запустить", error);
            }
            state = WaitForState(ServiceState.Running, CancellationToken.None);
        }

        if (state != ServiceState.Running)
            throw new InvalidOperationException("Windows не подтвердила запуск службы zapret.");
        restoreRequired = false;
        return true;
    }

    private ServiceState WaitUntilStable(CancellationToken token) => WaitForState(null, token);

    private ServiceState WaitForState(ServiceState? desired, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!QueryServiceStatus(service, out var status))
                throw ServiceError("Проверить состояние", Marshal.GetLastPInvokeError());

            if (desired is { } target ? status.State == target :
                status.State is ServiceState.Stopped or ServiceState.Running or ServiceState.Paused)
                return status.State;
            if (watch.Elapsed >= Timeout)
                throw new TimeoutException("Служба zapret слишком долго меняет состояние.");
            Thread.Sleep(150);
        }
    }

    private static Win32Exception ServiceError(string action, int error) =>
        new(error, $"{action} службу zapret не удалось. {new Win32Exception(error).Message}");

    public void Dispose() => service.Dispose();

    private enum ServiceState : uint { Stopped = 1, Running = 4, Paused = 7 }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint Type;
        public ServiceState State;
        public uint ControlsAccepted, ExitCode, ServiceExitCode, CheckPoint, WaitHint;
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(ServiceHandle service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(ServiceHandle service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(ServiceHandle service, uint argumentCount, IntPtr arguments);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr service);
}
