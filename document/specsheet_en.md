# usleep_win_cs Internal Specification

> Version: 0.1.x  
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

- **`SetThreadInformation`**  
  Passes `THREAD_POWER_THROTTLING_STATE` by `ref` to configure thread power throttling.

### 3.2 Unity Windows Build: `DllImport` (`USLP_WINDOWS`)

Uses the classic `DllImport` approach with `[SuppressUnmanagedCodeSecurity]` to skip the security stack walk, reducing call overhead.

### 3.3 Unity Generic Build

No P/Invoke. All Win32 branches are excluded from compilation.

---

## 4. Timestamp Acquisition (`NowUs()`)

`InternalTiming.NowUs()` returns the current monotonic clock time in microseconds.

### Priority Order

```
1. QPC path (USLP_WINDOWS || USLP_GENERATOR, _isWin == true, _qpcFreq > 0)
   QueryPerformanceCounter(out c)
   → (ulong)(c.QuadPart * 1_000_000L / _qpcFreq)

2. Stopwatch path (Stopwatch.IsHighResolution == true)
   Stopwatch.GetTimestamp() * _tickToUs
   ※ _tickToUs = 1_000_000.0 / Stopwatch.Frequency (cached at static init)

3. TickCount fallback (Stopwatch.IsHighResolution == false)
   (ulong)(uint)Environment.TickCount * 1000UL
   ※ Millisecond → microsecond conversion; 1 ms resolution
```

### Accuracy Notes

- QPC: typically ±1 µs or better. Frequency is cached in `_qpcFreq` at startup.
- Stopwatch (high-resolution): equivalent to QPC (most environments use QPC internally).
- TickCount fallback: 1 ms granularity; used only on non-Windows or non-high-resolution systems.

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
| `_qpcFreq` | `long` | QPC frequency (cached at startup) |
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
| `SuppressGCTransition` | Enabled (QPC calls) | Disabled | — |
| `SuppressUnmanagedCodeSecurity` | Disabled (not needed) | Enabled | — |

`USLP_X64_ONLY` is not normally set for Unity builds since they must support multiple platforms.

---

## 13. Security and Safety

- **P/Invoke targets**: Only `kernel32.dll` and `winmm.dll` — both standard Windows system DLLs.
- **String arguments**: The `lpTimerName` parameter passed to `CreateWaitableTimerEx` / `CreateWaitableTimer` is always `null`. Named timers are not used.
- **Internal exception handling**: `EntryPointNotFoundException` is caught inside `GetTimer()` and does not propagate to callers.
- **No cross-thread handle sharing**: `[ThreadStatic]` ensures each thread owns its timer handle exclusively.
- **Timer resolution double-call prevention**: `_timerResolutionLock` serializes `timeBeginPeriod` calls, preventing race conditions.
- **Integer overflow in `NowUs()`**: The QPC calculation `c.QuadPart * 1_000_000L / _qpcFreq` uses `long` arithmetic. The QPC counter would not reach `long.MaxValue / 1_000_000` for thousands of years, making overflow a non-issue in practice.

---

*This specification references source files under `src/`. If a discrepancy exists between this document and the implementation, the source code is authoritative.*
