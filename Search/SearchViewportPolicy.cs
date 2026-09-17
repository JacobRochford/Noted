namespace Noted.Search;

internal readonly record struct SearchViewportAxis(
    double Offset,
    double ViewportLength,
    double ExtentLength,
    double ResultStart,
    double ResultEnd,
    double ContextLength);

internal readonly record struct SearchViewportAdjustment(
    double Offset,
    bool RequiresScroll);

internal static class SearchViewportPolicy
{
    private const double FarJumpViewportFraction = 0.5;
    private const double UpperThirdFraction = 1.0 / 3.0;

    internal static SearchViewportAdjustment Resolve(SearchViewportAxis axis)
    {
        if (!IsFinite(axis.Offset) ||
            !IsFinite(axis.ViewportLength) ||
            !IsFinite(axis.ExtentLength) ||
            !IsFinite(axis.ResultStart) ||
            !IsFinite(axis.ResultEnd) ||
            !IsFinite(axis.ContextLength) ||
            axis.ViewportLength <= 0 ||
            axis.ExtentLength <= axis.ViewportLength)
        {
            return new SearchViewportAdjustment(axis.Offset, false);
        }

        var maximumOffset = Math.Max(0, axis.ExtentLength - axis.ViewportLength);
        var currentOffset = Math.Clamp(axis.Offset, 0, maximumOffset);
        var context = Math.Clamp(axis.ContextLength, 0, axis.ViewportLength / 2);
        var safeStart = currentOffset + context;
        var safeEnd = currentOffset + axis.ViewportLength - context;

        if (axis.ResultStart >= safeStart && axis.ResultEnd <= safeEnd)
            return new SearchViewportAdjustment(currentOffset, false);

        var distanceOutsideSafeViewport = axis.ResultEnd < safeStart
            ? safeStart - axis.ResultEnd
            : axis.ResultStart - safeEnd;
        double requestedOffset;

        if (distanceOutsideSafeViewport > axis.ViewportLength * FarJumpViewportFraction)
        {
            requestedOffset = axis.ResultStart - (axis.ViewportLength * UpperThirdFraction);
        }
        else if (axis.ResultStart < safeStart)
        {
            requestedOffset = axis.ResultStart - context;
        }
        else
        {
            requestedOffset = axis.ResultEnd + context - axis.ViewportLength;
        }

        var resolvedOffset = Math.Clamp(requestedOffset, 0, maximumOffset);
        return new SearchViewportAdjustment(
            resolvedOffset,
            Math.Abs(resolvedOffset - currentOffset) >= 0.01);
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}
