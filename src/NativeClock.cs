// SPDX-License-Identifier: MIT
#if !USLP_UNITY

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Usleep.Win;

/// <summary>
/// KUSER_SHARED_DATA 直読みにより P/Invoke ゼロ・~1ns でタイムスタンプを取得する。
/// 信頼性検証失敗時は Stopwatch.GetTimestamp() にフォールバックする。
/// </summary>
internal static unsafe class NativeClock
{
    private const ulong KuserSharedDataAddr = 0x7FFE0000UL;
    private static readonly byte* _ksd;
    private static readonly bool  _isReliable;

    static NativeClock()
    {
        // Windows 10 1803 未満はフォールバック専用
        if (Environment.OSVersion.Version < new Version(10, 0, 17134))
        {
            _isReliable = false;
            return;
        }

        byte* ptr = (byte*)KuserSharedDataAddr;
        try
        {
            _ = Unsafe.ReadUnaligned<long>(ptr + 0x3B8);
            long ksd = ReadRaw(ptr);
            long qpc = System.Diagnostics.Stopwatch.GetTimestamp();
            _isReliable = Math.Abs(ksd - qpc)
                          < System.Diagnostics.Stopwatch.Frequency / 1_000_000;
        }
        catch (AccessViolationException)
        {
            _isReliable = false;
        }

        _ksd = _isReliable ? ptr : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetTimestamp()
    {
        if (_ksd == null)
            return System.Diagnostics.Stopwatch.GetTimestamp();

        long bias  = Unsafe.ReadUnaligned<long>(_ksd + 0x3B8);
        byte shift = Unsafe.Read<byte>(_ksd + 0x3C4);
        Interlocked.MemoryBarrierProcessWide();
        return (long)((ulong)bias >> shift);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ReadRaw(byte* ptr)
    {
        long bias  = Unsafe.ReadUnaligned<long>(ptr + 0x3B8);
        byte shift = Unsafe.Read<byte>(ptr + 0x3C4);
        return (long)((ulong)bias >> shift);
    }
}

#endif
