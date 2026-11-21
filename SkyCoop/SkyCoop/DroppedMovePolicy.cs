namespace SkyCoop;

internal static class DroppedMovePolicy
{
    internal enum Result { Missing, Conflict, Unchanged, Apply }

    internal static Result Decide(bool hasVisual, bool hasData, bool originalMatches, bool targetMatches, bool variantChanged)
    {
        if (!hasVisual || !hasData) return Result.Missing;
        if (!originalMatches) return Result.Conflict;
        return targetMatches && !variantChanged ? Result.Unchanged : Result.Apply;
    }

    internal static bool ReturnPreviewOnRestore(bool confirming, bool isPreview) => !confirming && isPreview;
}
