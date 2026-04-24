namespace WebullAnalytics.AI;

internal static class DirectionalFit
{
    /// <summary>Returns +1 (bullish fit), −1 (bearish fit), or 0 (neutral) for the given structure.</summary>
    public static int SignFor(OpenStructureKind kind) => kind switch
    {
        OpenStructureKind.LongCall => 1,
        OpenStructureKind.ShortPutVertical => 1,
        OpenStructureKind.LongPut => -1,
        OpenStructureKind.ShortCallVertical => -1,
        OpenStructureKind.LongCalendar => 0,
        OpenStructureKind.LongDiagonal => 0,
        _ => 0
    };
}
