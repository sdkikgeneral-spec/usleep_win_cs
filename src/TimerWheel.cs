// SPDX-License-Identifier: MIT
#if !USLP_UNITY

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Usleep.Win;

/// <summary>
/// Magic Number 除算（Math.BigMul）で O(1) スロット管理。
/// </summary>
internal sealed class TimerWheel : IDisposable
{
    private const int SlotCount           = 4096;
    private const int SlotMask            = SlotCount - 1;
    private const int InitialSlotCapacity = 16;
    private const int MaxSlotCapacity     = 1024;

    private readonly PreciseWaitItem[][] _slots        = new PreciseWaitItem[SlotCount][];
    private readonly int[]               _slotCounts    = new int[SlotCount];
    private readonly int[]               _slotCapacities = new int[SlotCount];
    private bool _disposed;

    private readonly ulong _magicMultiplier;
    private readonly int   _magicShift;
    private readonly long  _baseTimestamp; // 構築時に固定。diff はオーバーフローしない（~29000年分）
    private int _currentSlot;

    public TimerWheel()
    {
        long ticksPerSlot = Stopwatch.Frequency / 1_000_000;
        (_magicMultiplier, _magicShift) = ComputeMagicNumbers(ticksPerSlot);
        _baseTimestamp = NativeClock.GetTimestamp();

        for (int i = 0; i < SlotCount; i++)
        {
            _slots[i]          = new PreciseWaitItem[InitialSlotCapacity];
            _slotCapacities[i] = InitialSlotCapacity;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(long nowTimestamp)
    {
        int targetSlot = ToSlotIndex(nowTimestamp);

        while (_currentSlot != targetSlot)
        {
            int slot = _currentSlot & SlotMask;
            if (_slotCounts[slot] > 0)
                CompleteSlot(slot);
            _currentSlot = (_currentSlot + 1) & SlotMask;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CompleteSlot(int slot)
    {
        int count = _slotCounts[slot];
        var items = _slots[slot];

        for (int i = 0; i < count; i++)
        {
            var item = items[i];
            if (item is null || !item.IsInitialized) continue;
            if (item.CancellationRequested) item.CompleteAsCancelled();
            else                            item.Complete();
            items[i] = null!;
        }
        _slotCounts[slot] = 0;
    }

    public void Enqueue(PreciseWaitItem item, long deadlineTimestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(TimerWheel));
        int slot      = ToSlotIndex(deadlineTimestamp);
        int count     = _slotCounts[slot];
        if ((uint)count >= (uint)_slotCapacities[slot]) GrowSlot(slot);
        _slots[slot][count] = item;
        _slotCounts[slot]   = count + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ToSlotIndex(long timestamp)
    {
        long diff = timestamp - _baseTimestamp;
        if (diff < 0)
        {
            Debug.Assert(false, $"timestamp が古い: diff={diff}");
            return 0;
        }
        ulong high = Math.BigMul((ulong)diff, _magicMultiplier, out _);
        return (int)(high >> (_magicShift - 64)) & SlotMask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowSlot(int slot)
    {
        int current = _slotCapacities[slot];
        int next    = Math.Min(current * 2, MaxSlotCapacity);
        if (next == current)
            throw new InvalidOperationException(
                $"スロット最大容量 {MaxSlotCapacity} に達しました");

        var newArr = new PreciseWaitItem[next];
        Array.Copy(_slots[slot], newArr, current);
        _slots[slot]          = newArr;
        _slotCapacities[slot] = next;
    }

    private static (ulong multiplier, int shift) ComputeMagicNumbers(long divisor)
    {
        int     shift      = 64 + BitOperations.Log2((ulong)divisor) + 1;
        UInt128 pow        = UInt128.One << shift;
        ulong   multiplier = (ulong)(pow / (ulong)divisor);
        return (multiplier, shift);
    }

    public void Dispose()
    {
        _disposed = true;
    }
}

#endif
