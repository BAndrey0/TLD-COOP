using System;

namespace TldCoop;

internal static class WaterWithdrawalPolicy
{
    internal static float Granted(float remaining, float requested) => Math.Min(remaining, requested);
}
