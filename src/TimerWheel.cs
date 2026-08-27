// SPDX-License-Identifier: MIT
#if !USLP_UNITY

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Usleep.Win;

/// <summary>
/// O(1) スロット管理のタイマーホイール。
/// 1 スロット = 1µs、スロット数ぶんの範囲を表現する。実際に表現できる長さは
/// <see cref="Stopwatch.Frequency"/> に依存するため、コンストラクタで
/// 必要な範囲を受け取って検証する。
/// </summary>
internal sealed class TimerWheel : IDisposable
{
    private const int SlotCount           = 8192;
    private const int SlotMask            = SlotCount - 1;
    private const int InitialSlotCapacity = 8;
    private const int MaxSlotCapacity     = 1024;

    private readonly PreciseWaitItem[][] _slots        = new PreciseWaitItem[SlotCount][];
    private readonly int[]               _slotCounts    = new int[SlotCount];
    private readonly int[]               _slotCapacities = new int[SlotCount];
    private bool _disposed;

    private readonly long _ticksPerSlot;
    private readonly long _baseTimestamp; // 構築時に固定。diff はオーバーフローしない（~29000年分）

    // 折り返しのない絶対スロット番号。Enqueue で「何スロット先か」を正しく
    // 判定するために保持する（マスク後の値だけでは前後関係が判別できない）。
    private long _currentSlot;

    /// <param name="requiredSpanMicroseconds">
    /// このホイールで扱う必要がある最大の待機時間（µs）。
    /// 実際に表現できる範囲がこれを下回る場合は構築に失敗する。
    /// </param>
    public TimerWheel(long requiredSpanMicroseconds)
    {
        // 以前はここで Magic Number 除算（Math.BigMul）を使っていたが、
        // 乗数の計算 2^(64+log2(d)+1) / d が ulong を溢れており（d=10 で 2.95e19）、
        // 下位 64bit だけが残った結果 1 スロットが 1µs ではなく約 2.67µs に
        // なっていた。long 除算は 1 回転あたり数 ns で、スピンループの
        // 1 反復（数十 ns）に対して支配的ではないため素直に除算する。
        _ticksPerSlot = Stopwatch.Frequency / 1_000_000;
        if (_ticksPerSlot < 1)
            throw new NotSupportedException(
                $"Stopwatch.Frequency={Stopwatch.Frequency} ではマイクロ秒解像度を表現できません");

        // 実際に表現できる範囲は Frequency に依存する。_ticksPerSlot は
        // 切り捨てなので、Frequency が 1MHz の倍数でない環境では 1 スロットが
        // 1µs 未満になり、スパンが SlotCount µs を下回る。ここを検証しないと
        // 「範囲内のつもりの deadline が折り返して早期完了する」状態に戻る。
        long spanUs = (long)SlotCount * _ticksPerSlot * 1_000_000L / Stopwatch.Frequency;
        if (spanUs < requiredSpanMicroseconds)
            throw new NotSupportedException(
                $"タイマーホイールの範囲 {spanUs}µs が必要な {requiredSpanMicroseconds}µs に届きません"
                + $"（Stopwatch.Frequency={Stopwatch.Frequency}）");

        _baseTimestamp = Stopwatch.GetTimestamp();
        _currentSlot   = 0; // _baseTimestamp のスロットが 0

        for (int i = 0; i < SlotCount; i++)
        {
            _slots[i]          = new PreciseWaitItem[InitialSlotCapacity];
            _slotCapacities[i] = InitialSlotCapacity;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(long nowTimestamp)
    {
        long targetSlot = ToSlotIndex(nowTimestamp);

        // 一周以上遅れている場合（スピンスレッドの起動直後やプリエンプト後）は、
        // 全スロットを 1 回だけ掃いて追いつく。素直に 1 スロットずつ進めると
        // 経過時間に比例して回り続けるため。
        if (targetSlot - _currentSlot >= SlotCount)
        {
            for (int i = 0; i < SlotCount; i++)
                if (_slotCounts[i] > 0)
                    CompleteSlot(i);
            _currentSlot = targetSlot;
            return;
        }

        while (_currentSlot < targetSlot)
        {
            int slot = (int)(_currentSlot & SlotMask);
            if (_slotCounts[slot] > 0)
                CompleteSlot(slot);
            _currentSlot++;
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

        long target = ToSlotIndex(deadlineTimestamp);

        if (target <= _currentSlot)
        {
            // 既に締切を過ぎている（スピンスレッドがプリエンプトされた後の
            // drain など）。ここで即完了させる。
            //
            // 「現在スロットへ入れる」方式は使わない。過去 deadline が N 件
            // 同時に来ると 1 スロットへ N 件集中し、MaxSlotCapacity 超過で
            // GrowSlot が例外を投げてスピンスレッドごとプロセスが落ちるため。
            // Complete() はスピンスレッド単独で呼ぶ規約だが、Enqueue の
            // 呼び出し元は SpinLoop のみなのでこの規約は保たれている。
            if (item.CancellationRequested) item.CompleteAsCancelled();
            else                            item.Complete();
            return;
        }

        if (target - _currentSlot >= SlotCount)
        {
            // ホイールの表現範囲を超えている。そのまま入れると折り返して
            // 過去のスロットに落ち、要求より早く完了してしまう。早く返すより
            // 遅らせる方が待機の契約に沿うので、表現できる最遠へ丸める。
            //
            // SpinLoop は drain 中も Advance を回して _currentSlot を最新に
            // 保つため、通常この分岐へは来ない。Debug.Assert は置かない
            // （.NET の Assert 失敗はプロセス即死で、到達可能な経路に
            // 置くと Debug ビルドのテストホストごと落ちるため）。
            target = _currentSlot + SlotCount - 1;
        }

        int slot  = (int)(target & SlotMask);
        int count = _slotCounts[slot];
        if ((uint)count >= (uint)_slotCapacities[slot]) GrowSlot(slot);
        _slots[slot][count] = item;
        _slotCounts[slot]   = count + 1;
    }

    /// <summary>
    /// タイムスタンプを折り返しのない絶対スロット番号へ変換する。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ToSlotIndex(long timestamp)
    {
        long diff = timestamp - _baseTimestamp;
        if (diff < 0)
            return 0; // 構築より前の時刻。Enqueue 側で「過去」として扱われる

        return diff / _ticksPerSlot;
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

    public void Dispose()
    {
        _disposed = true;
    }
}

#endif
