// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

internal static class Platform
{
    internal static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
