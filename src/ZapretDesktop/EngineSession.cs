using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ZapretDesktop;

internal sealed class EngineSession : IDisposable
{
    private Process? process;
    private SafeFileHandle? job;
    public event Action<Process>? Exited;

    public static void EnsureNoOtherEngine()
    {
        var running = Process.GetProcessesByName("winws");
        try
        {
            if (running.Length != 0)
                throw new InvalidOperationException("Уже работает другой winws.exe. Если это старая служба Zapret, " +
                    "временно остановите службу zapret в «Службах» Windows и повторите подбор.");
        }
        finally
        {
            foreach (var instance in running)
                instance.Dispose();
        }
    }

    public async Task StartAsync(EngineFiles engine, Preset preset, CancellationToken token)
    {
        Stop();
        EnsureNoOtherEngine();
        string bin = Path.Combine(engine.Root, "bin");
        var launch = new ProcessStartInfo(Path.Combine(bin, "winws.exe"))
        {
            WorkingDirectory = bin,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in preset.Arguments)
            launch.ArgumentList.Add(PresetParser.Expand(argument, engine.Root));

        job ??= KillOnCloseJob.Create();
        var child = Process.Start(launch) ?? throw new InvalidOperationException("Не удалось запустить winws.exe.");
        process = child;
        try
        {
            if (!child.HasExited && !KillOnCloseJob.AssignProcessToJobObject(job, child.SafeHandle))
            {
                int error = Marshal.GetLastPInvokeError();
                if (!child.HasExited)
                    throw new Win32Exception(error, "Не удалось привязать winws к приложению.");
            }
            await Task.Delay(750, token);
            if (child.HasExited)
                throw new InvalidOperationException($"winws завершился с кодом {child.ExitCode}.");
            child.Exited += (_, _) => Exited?.Invoke(child);
            child.EnableRaisingEvents = true;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public bool Owns(Process candidate) => ReferenceEquals(process, candidate);
    public bool IsRunning => process is { HasExited: false };

    public void Stop()
    {
        var child = process;
        process = null;
        if (child is null)
            return;
        using (child)
        {
            if (child.HasExited)
                return;
            try { child.Kill(); }
            catch (InvalidOperationException) when (child.HasExited) { }
            if (!child.WaitForExit(1500))
                throw new InvalidOperationException("winws не остановился. Повторите попытку через несколько секунд.");
        }
    }

    public void Dispose()
    {
        job?.Dispose();
        process?.Dispose();
        process = null;
    }
}

internal static class KillOnCloseJob
{
    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;

    public static SafeFileHandle Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = KillOnJobClose } };
        if (SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimits>()))
            return job;
        int lastError = Marshal.GetLastPInvokeError();
        job.Dispose();
        throw new Win32Exception(lastError);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTimeLimit, JobTimeLimit;
        public uint Flags;
        public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
}
