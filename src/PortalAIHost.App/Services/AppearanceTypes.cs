namespace PortalAIHost.App.Services;

public enum ThemeChoice { System, Light, Dark }
public enum MaterialChoice { Mica, Acrylic, Solid }
public enum CornerChoice { Rounded, Square, ExtraRound }
public enum DensityChoice { Compact, Standard, Comfortable }
public enum MotionChoice { FollowWindows, Reduced, Off }

/// <summary>How the window looks. Defaults follow the shared design tokens; every value can be changed in Settings.</summary>
public sealed record AppearancePrefs
{
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;
    public MaterialChoice Material { get; init; } = MaterialChoice.Mica;
    public CornerChoice Corners { get; init; } = CornerChoice.Rounded;
    public DensityChoice Density { get; init; } = DensityChoice.Standard;

    /// <summary>In-app text size step on top of Windows text scaling: 0 = normal, 1 = larger, 2 = largest.</summary>
    public int TextStep { get; init; }

    public MotionChoice Motion { get; init; } = MotionChoice.FollowWindows;

    public static AppearancePrefs Default { get; } = new();
}

/// <summary>Pure rules that turn the choices into numbers, so they can be tested without a window.</summary>
public static class AppearanceMath
{
    public static double CardRadius(CornerChoice c) => c switch { CornerChoice.Square => 0, CornerChoice.ExtraRound => 16, _ => 8 };

    public static double ControlRadius(CornerChoice c) => c switch { CornerChoice.Square => 0, CornerChoice.ExtraRound => 8, _ => 4 };

    public static double CardPadding(DensityChoice d) => d switch { DensityChoice.Compact => 12, DensityChoice.Comfortable => 24, _ => 16 };

    public static double RowGap(DensityChoice d) => d switch { DensityChoice.Compact => 6, DensityChoice.Comfortable => 14, _ => 10 };

    /// <summary>Body text size for a step (the Windows default is 14).</summary>
    public static double BodyFontSize(int step) => 14 * (1 + 0.12 * Math.Clamp(step, 0, 2));

    /// <summary>True when slides and fades should be skipped: the user asked for it, or Windows has animations off.</summary>
    public static bool ReducedMotion(MotionChoice choice, bool windowsAnimationsOn) =>
        choice != MotionChoice.FollowWindows || !windowsAnimationsOn;
}

/// <summary>Applies appearance choices to the live window. Implemented by the window; tests use a recorder.</summary>
public interface IAppearanceApplier
{
    void Apply(AppearancePrefs prefs);
}
