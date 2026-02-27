using System.Runtime.CompilerServices;
#if USLP_GENERATOR
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics.Arm;
#endif

internal static class SpinHints
{
#if USLP_GENERATOR
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
    internal static void HintOnce()
    {
#if USLP_GENERATOR
#if USLP_X64_ONLY
        X86Base.Pause();
#else
        if (X86Base.IsSupported) { X86Base.Pause(); return; }
        if (ArmBase.IsSupported) { ArmBase.Yield(); return; }
        System.Threading.SpinWait sw = default; sw.SpinOnce();
#endif
#else
        System.Threading.SpinWait sw = default; sw.SpinOnce();
#endif
    }

#if USLP_GENERATOR
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
    internal static void HintFewTimes(int n = 3)
    {
#if USLP_GENERATOR
#if USLP_X64_ONLY
        for (int i = 0; i < n; i++) X86Base.Pause();
#else
        if (X86Base.IsSupported)
        {
            for (int i = 0; i < n; i++) X86Base.Pause();
            return;
        }
        if (ArmBase.IsSupported)
        {
            for (int i = 0; i < n; i++) ArmBase.Yield();
            return;
        }
        for (int i = 0; i < n; i++)
        {
            System.Threading.SpinWait sw = default;
            sw.SpinOnce();
        }
#endif
#else
        for (int i = 0; i < n; i++)
        {
            System.Threading.SpinWait sw = default;
            sw.SpinOnce();
        }
#endif
    }
}
