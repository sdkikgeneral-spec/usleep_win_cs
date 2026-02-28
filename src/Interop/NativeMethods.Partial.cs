#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Security;

internal static partial class NativeMethods
{
    // Common constants used from managed side
    internal const uint TIMER_ALL_ACCESS = 0x1F0003;
    internal const uint CREATE_WAITABLE_TIMER_MANUAL_RESET = 0x00000001;
    internal const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;

    // Power throttling constants
    internal const int ThreadPowerThrottling = 11;
    internal const uint THREAD_POWER_THROTTLING_CURRENT_VERSION = 1;
    internal const uint THREAD_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct LARGE_INTEGER { public long QuadPart; }

#if USLP_GENERATOR
    // ==============================
    // .NET 10+ (NuGet): LibraryImport
    // ==============================
    [SuppressGCTransition]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryPerformanceCounter(out LARGE_INTEGER lpPerformanceCount);

    [SuppressGCTransition]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryPerformanceFrequency(out LARGE_INTEGER lpFrequency);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateWaitableTimerEx(
        IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateWaitableTimer(
        IntPtr lpTimerAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, string? lpTimerName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWaitableTimer(
        IntPtr hTimer, ref LARGE_INTEGER pDueTime, int lPeriod,
        IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool fResume);

    [LibraryImport("kernel32.dll")]
    internal static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll")]
    internal static partial void Sleep(uint dwMilliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SwitchToThread();

    [StructLayout(LayoutKind.Sequential)]
    internal struct THREAD_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetThreadInformation(
        IntPtr hThread, int ThreadInformationClass,
        ref THREAD_POWER_THROTTLING_STATE ThreadInformation, uint ThreadInformationSize);

    [LibraryImport("winmm.dll", SetLastError = true)]
    internal static partial uint timeBeginPeriod(uint uPeriod);

    [LibraryImport("winmm.dll", SetLastError = true)]
    internal static partial uint timeEndPeriod(uint uPeriod);

#elif USLP_WINDOWS
    // ==============================
    // Unity (Windows flavor): DllImport
    // ==============================
    [SuppressUnmanagedCodeSecurity]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryPerformanceCounter(out LARGE_INTEGER lpPerformanceCount);

    [SuppressUnmanagedCodeSecurity]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryPerformanceFrequency(out LARGE_INTEGER lpFrequency);

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateWaitableTimerEx(
        IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateWaitableTimer(
        IntPtr lpTimerAttributes, bool bManualReset, string? lpTimerName);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetWaitableTimer(
        IntPtr hTimer, ref LARGE_INTEGER pDueTime, int lPeriod,
        IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

    [DllImport("kernel32.dll")]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    internal static extern bool CloseHandle(IntPtr hObject);

    [SuppressUnmanagedCodeSecurity]
    [DllImport("kernel32.dll")]
    internal static extern void Sleep(uint dwMilliseconds);

    [SuppressUnmanagedCodeSecurity]
    [DllImport("kernel32.dll")]
    internal static extern bool SwitchToThread();

    [StructLayout(LayoutKind.Sequential)]
    internal struct THREAD_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetThreadInformation(
        IntPtr hThread, int ThreadInformationClass,
        ref THREAD_POWER_THROTTLING_STATE ThreadInformation, uint ThreadInformationSize);

    [DllImport("winmm.dll", SetLastError = true)]
    internal static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", SetLastError = true)]
    internal static extern uint timeEndPeriod(uint uPeriod);

#else
    // ==============================
    // Non‑Windows / generic Unity: stub (no externs)
    // ==============================
    // Intentionally empty: all calls are guarded by Platform.IsWindows inside the logic.
#endif
}
