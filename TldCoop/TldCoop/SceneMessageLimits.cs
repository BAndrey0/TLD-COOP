namespace TldCoop;

internal static class SceneMessageLimits
{
    internal static int PayloadLimit(byte kind) => kind switch
    {
        1 or 2 or 19 or 21 => 4096,
        9 or 17 or 18 => 12000,
        _ => 1100
    };
}
