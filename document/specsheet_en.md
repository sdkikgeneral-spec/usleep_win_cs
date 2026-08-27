# usleep_win_cs Internal Specification

> Version: 0.2.x
> Audience: Library developers and contributors

[日本語版](specsheet.md)

---

## Table of Contents

1. [Design Principles](#1-design-principles)
2. [Build Variants (Preprocessor Constants)](#2-build-variants-preprocessor-constants)
3. [P/Invoke Strategy](#3-pinvoke-strategy)
4. [Timestamp Acquisition (`NowUs()`)](#4-timestamp-acquisition-nowus)
5. [WaitableTimer Management](#5-waitabletimer-management)
6. [Sleep Algorithm](#6-sleep-algorithm)
7. [CPU Hint Instructions (`SpinHints`)](#7-cpu-hint-instructions-spinhints)
8. [Timer Resolution Management](#8-timer-resolution-management)
9. [Thread-Local State](#9-thread-local-state)
10. [Profile Behavior Details](#10-profile-behavior-details)
11. [Statistics Counters](#11-statistics-counters)
12. [Differences in Unity Builds](#12-differences-in-unity-builds)
13. [Security and Safety](#13-security-and-safety)
14. [PreciseDelay: High-Precision Async Timer](#14-precisedelay-high-precision-async-timer)

---

## 1. Design Principles

### 1.1 Goals

- Provide **practical-precision** microsecond-order short waits on Windows
- Allow CPU load vs. jitter trade-off to be switched via profiles
- Implement entirely in **pure C#** without unsafe blocks; use only P/Invoke to call OS APIs
- Maintain a single source tree that builds both for NuGet (`net10.0-windows`) and Unity (`netstandard2.1`)

### 1.2 Constraints

- **No hard real-time guarantees.** Windows is a soft real-time OS; behavior is affected by the scheduler, power management, and virtualization.
- Timer resolution changes (`timeBeginPeriod`) modify a system-wide setting.
- All settings are thread-local and are not inherited across threads.

---

## 2. Build Variants (Preprocessor Constants)

Compile-time constants select the implementation appropriate for each target environment.

| Constant | Applied Build | Effect |
|---|---|---|
| `USLP_GENERATOR` | NuGet (`net10.0-windows`) | Uses `LibraryImport` source generator. Enables `AggressiveOptimization` and `SkipLocalsInit` |
| `USLP_WINDOWS` | Unity Windows-only DLL | Uses `DllImport` + `SuppressUnmanagedCodeSecurity` |
| `USLP_X64_ONLY` | NuGet x64-only build (optional) | Calls `X86Base.Pause()` directly without runtime branching |
| `USLP_NUGET` | NuGet build identifier | Currently used alongside `USLP_GENERATOR`; reserved for future conditional use |
| `USLP_UNITY` | Unity DLL (both variants) | Excludes the 4 `PreciseDelay`-related files from compilation (`#if !USLP_UNITY`) |

**Generic build (neither constant defined):**  
All Win32 API calls are excluded at compile time. `Platform.IsWindows` always returns `false`, so WaitableTimer, QPC, and Sleep APIs are never called. The implementation falls back to `Thread.Yield()` / `Thread.Sleep()` / `Stopwatch`.

### Constant Combinations and Target Mapping

| Target | Constants Defined |
|---|---|
| NuGet (`net10.0-windows`) | `USLP_WINDOWS` + `USLP_NUGET` + `USLP_GENERATOR` |
| Unity Windows-only DLL | `USLP_WINDOWS` |
| Unity Generic DLL | (none) |

---

## 3. P/Invoke Strategy

### 3.1 NuGet Build: `LibraryImport` (`USLP_GENERATOR`)

Uses the source generator approach available since .NET 7. Marshalling code is generated at compile time.

- **`QueryPerformanceCounter` / `QueryPerformanceFrequency`**  
  Decorated with `[SuppressGCTransition]`. Eliminates the GC-safe-point transition cost, minimizing overhead in the hot path.

- **`CreateWaitableTimerEx`**  
  Explicitly specifies the Unicode entry point `CreateWaitableTimerExW` with `StringMarshalling.Utf16`.

- **`SetWaitableTimer`**
  Passes the due time as `ref LARGE_INTEGER` (negative value in 100-ns units = relative time).

- **`CreateWaitableTimerExSafe` / `SetWaitableTimerSafe`** (`SafeWaitHandle` overloads)
  Used exclusively by `PreciseDelay.WaitableTimerAsync` (the async fallback path). These variants accept/return a `SafeWaitHandle` instead of `IntPtr`.
  The actual entry points are `CreateWaitableTimerExW` and `SetWaitableTimer` respectively (declared via the `EntryPoint` attribute).

- **`SetThreadInformation`**
  Passes `THREAD_POWER_THROTTLING_STATE` by `ref` to configure thread power throttling. See 3.4 for details.

### 3.2 Unity Windows Build: `DllImport` (`USLP_WINDOWS`)

Uses the classic `DllImport` approach with `[SuppressUnmanagedCodeSecurity]` to skip the security stack walk, reducing call overhead.

### 3.3 Unity Generic Build

No P/Invoke. All Win32 branches are excluded from compilation.

### 3.4 `SetPowerMode(UsleepPowerMode)` Implementation

The `ThreadPowerThrottling` value of `THREAD_INFORMATION_CLASS` is enum value **3**
(the `ThreadPowerThrottling` constant in `src/Interop/NativeMethods.Partial.cs`).

> This constant was previously (incorrectly) set to `11`, which caused
> `SetThreadInformation` to fail with `ERROR_INVALID_PARAMETER`, so `SetPowerMode`
> always returned `false` regardless of the requested mode. The upstream C++
> implementation had already fixed the same bug separately; the fix had not been
> ported to the C# side.

Out-of-range `enum` values are rejected via `Enum.IsDefined(typeof(UsleepPowerMode), mode)`
and return `false` immediately. On non-Windows builds this always returns `false`.

`THREAD_POWER_THROTTLING_STATE`'s `ControlMask` (which throttling categories the caller
controls) and `StateMask` (whether to enable them) are set per mode as follows:

| Mode | `ControlMask` | `StateMask` | Behavior |
|---|---|---|---|
| `ECO` | `THREAD_POWER_THROTTLING_EXECUTION_SPEED` | `THREAD_POWER_THROTTLING_EXECUTION_SPEED` | Enable execution-speed throttling under caller control |
| `PERF` | `THREAD_POWER_THROTTLING_EXECUTION_SPEED` | `0` | Take control, but keep throttling disabled (performance-preferred) |
| `DEFAULT` | `0` | `0` | Release caller control and restore OS default behavior |

> `DEFAULT` previously left `ControlMask` set, so it never actually restored the OS
> default and behaved identically to `PERF`. The three modes are now clearly
> distinguished.

**Applies only to the calling thread.** It has no effect on the `PreciseDelay` spin
thread (`SpinCoreEngine`) or any other thread.

---

## 4. Timestamp Acquisition (`NowUs()`)

`InternalTiming.NowUs()` returns the current monotonic clock time in microseconds.

### Priority Order

```
[USLP_GENERATOR — NuGet build]

1. Stopwatch path (Stopwatch.IsHighResolution == true)
   Stopwatch.GetTimestamp() * _tickToUs
   ※ Stopwatch.GetTimestamp() internally calls QPC (measured ~20 ns/call)

2. TickCount fallback (Stopwatch.IsHighResolution == false)
   (ulong)(uint)Environment.TickCount * 1000UL

[USLP_WINDOWS — Unity Windows build]

1. QPC path (_isWin == true, _qpcFreq > 0)
   QueryPerformanceCounter(out c)
   → (ulong)(c.QuadPart * 1_000_000L / _qpcFreq)

2. Stopwatch path (Stopwatch.IsHighResolution == true)
   Stopwatch.GetTimestamp() * _tickToUs

3. TickCount fallback (Stopwatch.IsHighResolution == false)
   (ulong)(uint)Environment.TickCount * 1000UL

[Neither constant defined — Generic build]

Same as steps 2–3 of the USLP_WINDOWS block above (Stopwatch path → TickCount fallback).
```

> An earlier version used a `NativeClock` class that read `KUSER_SHARED_DATA`
> (`0x7FFE0000`) directly as the preferred path in NuGet builds. It has been removed
> (`src/NativeClock.cs` no longer exists). Measurement showed the offset `0x3B8` it
> read as a "monotonically increasing counter" (intended as `QpcBias`) had a delta of
> 0 even after 50 ms had elapsed, and the offset `0x3C4` read as the shift value was
> actually `ActiveGroupCount`, not the real `QpcShift` (`0x3C7`). As a result, the
> startup reliability check always failed and the code fell back to
> `Stopwatch.GetTimestamp()` on every call anyway. The reliability check itself relied
> on `Interlocked.MemoryBarrierProcessWide()`, measured at 88 ns/call — slower than
> `Stopwatch.GetTimestamp()`'s measured 19.7 ns/call — so the "~1 ns, zero P/Invoke"
> premise never held.

### Accuracy Notes

- QPC (Unity Windows): typically ±1 µs or better. Frequency is cached in `_qpcFreq` at startup.
- Stopwatch (high-resolution, the only path used in NuGet builds): equivalent to QPC (most environments use QPC internally; measured ~20 ns/call).
- TickCount fallback: 1 ms granularity; used only when `Stopwatch.IsHighResolution == false`.

None of these paths provide any accuracy guarantee. Windows is not a hard real-time
OS; measured results vary with scheduler, power management, and virtualization.

---

## 5. WaitableTimer Management

### 5.1 Thread-Local Handles

`_tTimer` is a `[ThreadStatic]` field in `InternalTiming`. Each thread holds its own timer handle, eliminating inter-thread contention.

> Handle cleanup (`CloseHandle`) relies on the OS reclaiming resources at thread exit. No explicit close is performed, and handles are not recreated during a thread's lifetime.

### 5.2 Acquisition Flow (`GetTimer()`)

```
if _tTimer != IntPtr.Zero → return cached handle

Check _createWaitableTimerExState:
  >= 0 → attempt CreateWaitableTimerEx
  <  0 → CreateWaitableTimerEx unavailable (EntryPointNotFoundException caught previously)

1. CreateWaitableTimerEx(NULL, NULL, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS)
   success → store in _tTimer; set _createWaitableTimerExState = 1

2. fail → CreateWaitableTimerEx(NULL, NULL, 0, TIMER_ALL_ACCESS) (no flag)

3. EntryPointNotFoundException caught → set _createWaitableTimerExState = -1

Final fallback:
   _tTimer == IntPtr.Zero → CreateWaitableTimer(NULL, false, NULL)
```

`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` (`0x00000002`) is available from Windows 10 version 1803 (RS4) onward. This flag improves timer wake-up accuracy.

### 5.3 Timer Usage Pattern

```csharp
// due is a negative value in 100-ns units (relative time)
var due = new LARGE_INTEGER { QuadPart = -(coarseUs * 10L) };
SetWaitableTimer(h, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
WaitForSingleObject(h, 0xFFFFFFFF); // INFINITE
```

When `tailSpinUs > 0`, `coarseUs = usec - tailSpinUs` is used, waking the timer slightly early and compensating the remaining time with a spin loop to reduce late wake-ups.

---

## 6. Sleep Algorithm

### 6.1 `SleepMicroseconds(usec)` Flow

```
usec == 0 → CoarseYield(SWITCH_THREAD) and return

Determine profile-specific thresholds:
  STRICT:    timerFirstUs=1500, preferSpinBelow=500
  LOW_POWER: timerFirstUs=1000, preferSpinBelow=0
  BALANCED:  timerFirstUs=2000, preferSpinBelow=200

if (usec >= timerFirstUs) OR (usec > preferSpinBelow):
    SleepByTimer(usec, tailSpinUs, policy, lowPower)
else:
    SpinWithPeriodicYield(NowUs() + usec, tailSpinUs, policy)
```

**Rationale:**
- `usec > preferSpinBelow`: use the timer for moderately long waits to avoid wasting CPU spin
- `usec >= timerFirstUs`: long enough for a pure timer wait (longer than the spin tail)
- Shorter waits: pure spin to minimize scheduling overhead

### 6.2 `SleepByTimer(usec, tailSpinUs, policy, lowPower)` Flow

```
targetUs = NowUs() + usec
h = GetTimer()

if h != IntPtr.Zero:
    coarseUs = (tailSpinUs > 0 && usec > tailSpinUs) ? usec - tailSpinUs : usec
    due = -(coarseUs * 10)
    if SetWaitableTimer(h, ref due, ...):
        tHrTimerUses++
        WaitForSingleObject(h, INFINITE)

        if tailSpinUs > 0:
            SpinWithPeriodicYield(targetUs, 0, NONE)  // pure spin to cover remaining time
        elif !lowPower:
            while NowUs() < targetUs: HintOnce()      // short spin (no tail, non-low-power)
        return

// Timer handle unavailable or SetWaitableTimer failed
if usec >= 1000:
    Sleep(usec / 1000)  // truncated to ms
    tYieldSleep1++
    if tailSpinUs > 0: SpinWithPeriodicYield(targetUs, ...)
    return

SpinWithPeriodicYield(targetUs, tailSpinUs, policy)
```

### 6.3 `SpinWithPeriodicYield(targetUs, tailSpinUs, policy)` Flow

```
ctr = 0
loop:
    now = NowUs()
    if now >= targetUs: break
    remain = targetUs - now

    if remain > tailSpinUs:          // not yet in tail spin window
        if (++ctr & 63) == 0:        // yield to OS every 64 iterations
            CoarseYield(policy)
        else:
            HintFewTimes(3)          // 3 spin hints
            tSpinRelax += 3
    else:                            // tail spin window (near deadline)
        HintOnce()                   // minimal spin
        tSpinRelax++
```

**Design rationale for "yield every 64 iterations":**  
This frequency prevents the spin loop from monopolizing the CPU core while keeping jitter minimal.

---

## 7. CPU Hint Instructions (`SpinHints`)

### 7.1 Branch Matrix

| Build | `X86Base.IsSupported` | `ArmBase.IsSupported` | Executed |
|---|---|---|---|
| `USLP_GENERATOR` + `USLP_X64_ONLY` | — | — | `X86Base.Pause()` directly |
| `USLP_GENERATOR` (generic) | true | — | `X86Base.Pause()` |
| `USLP_GENERATOR` (generic) | false | true | `ArmBase.Yield()` |
| `USLP_GENERATOR` (generic) | false | false | `SpinWait.SpinOnce()` |
| Other (Unity, etc.) | — | — | `SpinWait.SpinOnce()` |

### 7.2 Effect of Each Instruction

- **`x86 PAUSE`**: In SMT (Hyper-Threading) environments, reduces power consumption and memory order violation penalties in spin loops. Provides a hint to the CPU pipeline that this is a spin loop.
- **`ARM64 YIELD`**: Equivalent to x86 PAUSE. Hints the processor that another thread on the same core may make progress.
- **`SpinWait.SpinOnce()`**: Fallback; the .NET runtime issues an appropriate hint for the current environment.

### 7.3 `AggressiveOptimization` Attribute

Under `USLP_GENERATOR`, `MethodImplOptions.AggressiveOptimization` is applied to:
- `UsleepWin.SleepMicroseconds`
- `UsleepWin.SleepUntilSteadyMicroseconds`
- `InternalTiming.NowUs`
- `InternalTiming.SleepByTimer`
- `InternalTiming.SpinWithPeriodicYield`
- `InternalTiming.CoarseYield`
- `SpinHints.HintOnce`
- `SpinHints.HintFewTimes`

This encourages the JIT compiler to apply aggressive optimizations (loop unrolling, vectorization, etc.) to reduce spin loop overhead.

### 7.4 `SkipLocalsInit`

Under `USLP_GENERATOR`, `AssemblyAttributes.cs` applies `[module: SkipLocalsInit]`. This skips zero-initialization of local variables, reducing startup cost in the hot path.

---

## 8. Timer Resolution Management

### 8.1 API

- `timeBeginPeriod(ms)` / `timeEndPeriod(ms)`: `winmm.dll` APIs
- Changes the system-wide timer interrupt period (e.g., from the default ~15.6 ms to 1 ms)

### 8.2 Safety Measures

- `_timerResolutionMs` (global) and `_timerResolutionLock` protect state
- If `InitTimerResolution` is called again with the same value, returns `true` immediately
- If called with a different value, calls `timeEndPeriod` first, then re-applies the new value
- `ShutdownTimerResolution()` calls `timeEndPeriod` and resets `_timerResolutionMs = 0`

### 8.3 Side Effects and Cautions

- `timeBeginPeriod(1)` increases system-wide power consumption across all processes
- On Windows 10 v1803+, `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` is available and sufficient for most use cases, making `timeBeginPeriod` unnecessary
- Avoid using on battery-powered devices unless absolutely required

---

## 9. Thread-Local State

The following fields are all `[ThreadStatic]`. Each thread holds its own independent state.

### `UsleepWin` (Public Settings)

| Field | Type | Default | Description |
|---|---|---|---|
| `_profile` | `UsleepProfile` | `BALANCED` | Current active profile |
| `_tailSpinUs` | `uint` | `250` | Post-timer spin duration (µs) |
| `_yieldPolicy` | `UsleepYieldPolicy` | `SLEEP0` | Cooperative yield method |

### `InternalTiming` (Internal State)

| Field | Type | Description |
|---|---|---|
| `_tTimer` | `IntPtr` | Per-thread WaitableTimer handle |
| `tSpinRelax` | `ulong` | Spin hint usage count (stats) |
| `tYieldSwitch` | `ulong` | SwitchToThread yield count (stats) |
| `tYieldSleep0` | `ulong` | Sleep(0) yield count (stats) |
| `tYieldSleep1` | `ulong` | Sleep(1) yield count (stats) |
| `tHrTimerUses` | `ulong` | Waitable timer usage count (stats) |

### Global (Process-Shared)

| Field | Type | Description |
|---|---|---|
| `_timerResolutionMs` | `uint` | Currently requested timer resolution (ms); 0 = not set |
| `_timerResolutionLock` | `object` | Exclusive lock for `timeBeginPeriod` calls |
| `_qpcFreq` | `long` | QPC frequency (cached at startup). Used only in `USLP_WINDOWS` builds; fixed at 0 in NuGet builds. |
| `_isWin` | `bool` | Whether running on Windows (determined at startup) |
| `_hires` | `bool` | `Stopwatch.IsHighResolution` |
| `_tickToUs` | `double` | Stopwatch tick-to-µs conversion coefficient |
| `_createWaitableTimerExState` | `int` | `CreateWaitableTimerEx` availability (0: unknown, 1: available, -1: unavailable) |

---

## 10. Profile Behavior Details

### `BALANCED` (Default)

| Parameter | Value | Meaning |
|---|---|---|
| `timerFirstUs` | 2000 µs | Use timer for waits ≥ 2 ms |
| `preferSpinBelow` | 200 µs | Consider timer use for waits > 200 µs |
| `tailSpinUs` | 250 µs | Spin 250 µs after timer wake-up |
| `yieldPolicy` | `SLEEP0` | Yield via `Sleep(0)` during wait |

### `STRICT`

| Parameter | Value | Meaning |
|---|---|---|
| `timerFirstUs` | 1500 µs | Use timer more aggressively (≥ 1.5 ms) |
| `preferSpinBelow` | 500 µs | Attempt timer even for waits > 500 µs |
| `tailSpinUs` | 400 µs (min 300) | Longer spin tail for tighter deadline |
| `yieldPolicy` | `SWITCH_THREAD` | More responsive yield method |

> When `SetProfile(STRICT)` is called, `_tailSpinUs` is forced to 400 if it was below 300.

### `LOW_POWER`

| Parameter | Value | Meaning |
|---|---|---|
| `timerFirstUs` | 1000 µs | Use timer for waits ≥ 1 ms |
| `preferSpinBelow` | 0 µs | No spin-preferred window |
| `tailSpinUs` | 0 µs | No post-timer spin |
| `yieldPolicy` | `SLEEP1` | Most conservative yield |

When `lowPower == true`, after the timer wait, the overshoot check (`while NowUs() < targetUs`) is also skipped — the function returns immediately.

---

## 11. Statistics Counters

All counters are thread-local `ulong` fields. Overflow is not a practical concern (safe up to trillions of calls).

| Counter | When Incremented |
|---|---|
| `tSpinRelax` | +1 per `HintOnce()` call; +n per `HintFewTimes(n)` call |
| `tYieldSwitch` | +1 per `CoarseYield(SWITCH_THREAD)` |
| `tYieldSleep0` | +1 per `CoarseYield(SLEEP0)` |
| `tYieldSleep1` | +1 per `CoarseYield(SLEEP1)` or `Sleep(ms)` fallback |
| `tHrTimerUses` | +1 per successful `SetWaitableTimer` + `WaitForSingleObject` pair |

Passing `reset: true` to `GetStats()` atomically retrieves and zeros all counters.

---

## 12. Differences in Unity Builds

| Item | NuGet (`USLP_GENERATOR`) | Unity Windows (`USLP_WINDOWS`) | Unity Generic |
|---|---|---|---|
| Target Framework | `net10.0-windows` | `netstandard2.1` | `netstandard2.1` |
| P/Invoke | `LibraryImport` | `DllImport` | None |
| Win32 code paths | Active | Active | Disabled |
| `AggressiveOptimization` | Enabled | Disabled | Disabled |
| `SkipLocalsInit` | Enabled | Disabled | Disabled |
| CPU-specific instructions (PAUSE/YIELD) | Enabled (runtime branch) | `SpinWait` | `SpinWait` |
| `SuppressGCTransition` | Declared (QPC calls) / not used in hot path | Disabled | — |
| `SuppressUnmanagedCodeSecurity` | Disabled (not needed) | Enabled | — |

`USLP_X64_ONLY` is not normally set for Unity builds since they must support multiple platforms.

---

## 13. Security and Safety

- **P/Invoke targets**: Only `kernel32.dll` and `winmm.dll` — both standard Windows system DLLs.
- **String arguments**: The `lpTimerName` parameter passed to `CreateWaitableTimerEx` / `CreateWaitableTimer` is always `null`. Named timers are not used.
- **Internal exception handling**: `EntryPointNotFoundException` is caught inside `GetTimer()` and does not propagate to callers.
- **No cross-thread handle sharing**: `[ThreadStatic]` ensures each thread owns its timer handle exclusively.
- **Timer resolution double-call prevention**: `_timerResolutionLock` serializes `timeBeginPeriod` calls, preventing race conditions.
- **Integer overflow in `NowUs()` (Unity / `USLP_WINDOWS`)**: The QPC calculation `c.QuadPart * 1_000_000L / _qpcFreq` uses `long` arithmetic. The QPC counter would not reach `long.MaxValue / 1_000_000` for thousands of years, making overflow a non-issue in practice. In NuGet builds (`USLP_GENERATOR`), `Stopwatch.GetTimestamp() * _tickToUs` (floating-point multiplication) is used instead, so integer overflow likewise does not apply.
- **Internal visibility for tests**: In `USLP_GENERATOR` builds only, `src/AssemblyAttributes.cs` declares `[assembly: InternalsVisibleTo("UsleepWin.Tests")]`. `TimerWheel` and `PreciseWaitItem` remain `internal`, but boundary conditions (large bursts of past deadlines, deadlines beyond the wheel's representable range) cannot be reproduced through integration tests that go through `PreciseDelay`, so the test assembly is given direct access. This does not affect the visibility of any public API.

---

---

## 14. PreciseDelay: High-Precision Async Timer

### 14.1 Overview

`PreciseDelay` is a static class that provides **±1–3 µs** precision async waits — beyond what `UsleepWin` can achieve.
A dedicated spin thread, timer wheel, and `IValueTaskSource` pool combine to deliver **zero-allocation** high-precision scheduling.

NuGet target (`net10.0-windows`) only. All four source files are excluded from Unity DLL builds via `#if !USLP_UNITY`.

### 14.2 Class Overview

| Class / File | Role |
| --- | --- |
| `PreciseWaitItem` (`src/PreciseWaitItem.cs`) | Wait item using `IValueTaskSource` + `ObjectPool<T>`. Issues zero-allocation `ValueTask` via `ManualResetValueTaskSourceCore<bool>`. |
| `TimerWheel` (`src/TimerWheel.cs`) | 8192-slot timer wheel. O(1) slot calculation via plain `long` division. |
| `SpinCoreEngine` (`src/SpinCoreEngine.cs`) | Spin thread pinned to a dedicated CPU core. Minimizes system timer resolution with `NtSetTimerResolution(1)` and calls `TimerWheel.Advance()` in a tight loop. |
| `PreciseDelay` (`src/PreciseDelay.cs`) | Public API. Automatically routes to the spin path (≤5 ms) or WaitableTimer HR path (>5 ms). |

The time source is `Stopwatch.GetTimestamp()`. An earlier version had a separate
`NativeClock` class (`src/NativeClock.cs`) that read `KUSER_SHARED_DATA` directly;
it has been removed. See section 4 ("Timestamp Acquisition") for the reasoning.

### 14.3 When `PreciseWaitItem` Is Returned to the Pool

The item is **not** returned to the pool at the moment `Complete()` /
`CompleteAsCancelled()` runs. It is returned in `GetResult()` instead — i.e. once the
caller has received the result via `await`.

```csharp
public void GetResult(short token)
{
    try { _vtsc.GetResult(token); }
    finally { PreciseWaitItemPool.Return(this); }
}
```

**Reason:** If the item were returned at completion time (`Complete()`), another
thread could `Rent()` and `Reset()` the same instance before the original caller had
awaited it. `Reset()` calls `ManualResetValueTaskSourceCore<bool>.Reset()`, which
changes `_vtsc.Version`, so the not-yet-awaited `ValueTask` — which still holds the
old token — would break. Deferring the return to `GetResult()` guarantees the
instance is only reused once the caller has fully consumed the result.

`Complete()` / `CompleteAsCancelled()` are designed to be called only from `SpinLoop`
(the spin thread, single-threaded); no `Interlocked` operations are used. The
`IsInitialized` flag guards against use-after-free.

### 14.4 TimerWheel Design

#### Slot Calculation (Plain `long` Division)

`_ticksPerSlot = Stopwatch.Frequency / 1_000_000` (ticks per µs) is computed at
construction time, and the slot number is simply `diff / _ticksPerSlot`.

```text
diff = timestamp - _baseTimestamp   (_baseTimestamp is fixed at construction)
slot = diff / _ticksPerSlot
```

`diff` does not overflow `long` for roughly **~29,000 years** at a QPC frequency of
~10 MHz — not a practical concern.

> **Why the magic-number division (`Math.BigMul`) was removed**
>
> An earlier version avoided division by computing a magic multiplier — the
> reciprocal of `_ticksPerSlot` — via `ComputeMagicNumbers()` and using
> `(diff × magicMultiplier) >> (magicShift - 64)`. However the formula
> `2^(64+log2(d)+1) / d` overflows `ulong` (max `1.84 × 10^19`) when `d = 10`
> (10 ticks per µs; `Stopwatch.Frequency = 10 MHz` is the most common value on
> Windows, not an exotic configuration), producing `2.95 × 10^19`; only the lower
> 64 bits survived. The effective factor became `0.0375` rather than `1/10 = 0.1`,
> so each "slot" was actually about **2.67 µs** instead of 1 µs.
>
> The wheel's effective range therefore did not shrink — it grew by roughly 2.67×
> (about 10.9 ms for 4096 slots). The real defect was not early completion from
> insufficient range but that **wait granularity was quantized to 2.67 µs, so the
> ±1–3 µs target of `PreciseDelay` could not hold**. A plain `long`
> division costs only a few nanoseconds per call, not dominant compared to a single
> spin-loop iteration (which includes a `Stopwatch.GetTimestamp()` call, measured
> ~20 ns), so correctness was prioritized over the magic-number trick.

#### Runtime Span Validation in the Constructor

The `TimerWheel` constructor takes `requiredSpanMicroseconds` — the maximum wait
duration (µs) the wheel must be able to represent. Because the actual representable
range depends on `Stopwatch.Frequency` (`_ticksPerSlot` is computed via truncating
division, so on environments where the frequency is not a multiple of 1 MHz, a
single slot can be less than 1 µs), the constructor computes
`spanUs = SlotCount * _ticksPerSlot * 1_000_000 / Stopwatch.Frequency` and throws
`NotSupportedException` if it falls short of `requiredSpanMicroseconds`.
`Debug.Assert` is deliberately not used (it is stripped in Release builds, and a
failed assert terminates the .NET process immediately).

`SpinCoreEngine.Initialize()` performs this validation by passing
`PreciseDelay.SpinPathMaxMilliseconds` (5 ms).

#### Absolute Slot Number, `Advance()`, and `Enqueue()`

`_currentSlot` is kept as an absolute, non-wrapping slot number (the masked value
alone cannot determine ordering). `Advance()` walks forward one slot at a time up to
the slot corresponding to the current time. If more than a full lap has elapsed
(e.g. right after the spin thread starts, or after being preempted), it sweeps all
slots once to catch up instead of looping slot-by-slot.

If `Enqueue()` is called with a deadline at or before the current slot (i.e. already
past due), the item is completed on the spot via `Complete()` /
`CompleteAsCancelled()` instead of being queued. It deliberately does *not* place
such items into the current slot: a burst of past-due items arriving together would
concentrate into a single slot and exceed `MaxSlotCapacity` (1024), causing
`GrowSlot()` to throw and taking down the spin thread (and the process) with it.
`Complete()` is contractually only called from the spin thread, and since
`Enqueue()`'s only caller is `SpinLoop`, that contract is preserved.

If a deadline exceeds the wheel's representable range (`SlotCount` slots), it is
clamped to the farthest representable slot rather than wrapping around into a past
slot (which would complete early). `SpinLoop` calls `Advance()` even mid-drain of
`_incoming` to keep `_currentSlot` current, so this branch is normally not reached.

#### Dispose

`TimerWheel.Dispose()` merely sets the `_disposed` flag. The caller
(`SpinCoreEngine`) only invokes it once the spin thread's `Thread.Join()` has
succeeded (see 14.6).

### 14.5 WaitAsync Routing

```text
delay ≤ 0          → complete immediately (equivalent to ValueTask.CompletedTask)
0 < delay ≤ 5 ms   → enqueue into SpinCoreEngine (spin path)
delay > 5 ms       → WaitableTimerAsync (WaitableTimer HR path)
```

On the spin path, `PreciseWaitItemPool.Rent()` obtains a wait item and `TimerWheel.Enqueue()` registers it with a deadline. The SpinCoreEngine spin loop calls `TimerWheel.Advance()` to drain slots and resumes the awaiter via `PreciseWaitItem.Complete()` → `IValueTaskSource.SetResult()`.

On the WaitableTimer HR path, `ThreadPool.RegisterWaitForSingleObject` is used. The `SafeWaitHandle` is wrapped in an `EventWaitHandle` to satisfy the `WaitHandle` parameter requirement.

### 14.6 Lifecycle and Safety

| Operation | Condition | Exception |
| --- | --- | --- |
| `Initialize(cpuCore)` | `cpuCore == 0` | `ArgumentException` |
| `Initialize(cpuCore)` | Already initialized | `InvalidOperationException` |
| `WaitAsync(...)` | Not initialized / after Shutdown | `InvalidOperationException` |
| `WaitAsync(..., ct)` | `ct` already cancelled | `OperationCanceledException` |

Inside `Initialize()`, a temporary variable is used for the `SpinCoreEngine` instance; it is assigned to `_engine` only after `Initialize()` succeeds. This prevents `IsInitialized` from being `true` after a failed initialization.

`SpinCoreEngine.Dispose()` sets `_running = false` and then calls
`TimerWheel.Dispose()` **only if** `Thread.Join(TimeSpan.FromSeconds(1))` on the spin
thread succeeds. If a live spin thread were allowed to touch a `TimerWheel` whose
`_disposed` flag had already been set, the next `Enqueue()` would throw
`ObjectDisposedException` and take the spin thread (and the process) down with it.
If `Join` times out, the wheel is left for the GC instead of being disposed.

### 14.7 Related Files

| File | Content |
| --- | --- |
| [src/PreciseWaitItem.cs](../src/PreciseWaitItem.cs) | IValueTaskSource + ObjectPool wait item |
| [src/TimerWheel.cs](../src/TimerWheel.cs) | O(1) timer wheel (8192 slots, plain `long` division) |
| [src/SpinCoreEngine.cs](../src/SpinCoreEngine.cs) | Dedicated-core spin thread engine |
| [src/PreciseDelay.cs](../src/PreciseDelay.cs) | Public API (Initialize / Shutdown / WaitAsync) |
| [tests/UsleepWin.Tests/PreciseDelayTests.cs](../tests/UsleepWin.Tests/PreciseDelayTests.cs) | Integration tests for `PreciseDelay`, plus direct `TimerWheel` boundary-condition tests (via `InternalsVisibleTo`) |
| [tests/UsleepWin.UnityWindows.Tests](../tests/UsleepWin.UnityWindows.Tests) | Smoke tests for the Unity Windows variant (`USLP_UNITY` + `USLP_WINDOWS`), exercising the `DllImport` P/Invoke and QPC paths at run time. This runs on CoreCLR and is not a validation of Mono / IL2CPP |
| [document/test_result.md](test_result.md) | Test result report (snapshot from initial run; see the test project for the current test count) |

---

*This specification references source files under `src/`. If a discrepancy exists between this document and the implementation, the source code is authoritative.*
