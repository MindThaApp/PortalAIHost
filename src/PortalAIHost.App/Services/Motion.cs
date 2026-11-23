namespace PortalAIHost.App.Services;

/// <summary>
/// The shared motion token (design guide section 3): one curve, cubic-bezier(.45, 0, .1, 1), and one base duration.
/// Animations ask for these instead of choosing their own, and are skipped when motion is reduced or off.
/// </summary>
public static class Motion
{
    public const double CurveX1 = 0.45, CurveY1 = 0, CurveX2 = 0.1, CurveY2 = 1;

    /// <summary>Base length of a small transition.</summary>
    public static readonly TimeSpan Base = TimeSpan.FromMilliseconds(200);
}
