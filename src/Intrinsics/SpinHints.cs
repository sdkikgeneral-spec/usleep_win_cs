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
        if (X86Base.IsSupported) { X86Base.Pause(); return; }
        if (ArmBase.IsSupported) { ArmBase.Yield(); return; }
#endif
        System.Threading.SpinWait sw = default; sw.SpinOnce();
    }

#if USLP_GENERATOR
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
    internal static void HintFewTimes(int n = 3)
    {
        for (int i = 0; i < n; i++) HintOnce();
    }
}
