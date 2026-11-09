using Microsoft.Win32;
using NeoAstra.Rpc;
using Silvia.Core.Projects;

namespace Silvia.App.Rpc;

/// <summary>
/// App-wide appearance: theme, the two text sizes, line height, reading width, and the three font
/// slots. Stored in the catalog so the window can open in the right colors before the page loads.
/// </summary>
internal static class Appearance
{
    /// <summary>Bundled fonts, served from the app itself with no network access (SIL Open Font License).</summary>
    public static readonly IReadOnlyList<string> BundledFonts =
        ["Inter", "Source Serif 4", "JetBrains Mono", "Noto Sans Arabic", "Noto Naskh Arabic"];

    /// <summary>The slot value that means "use the system font stack" for that slot.</summary>
    public const string SystemDefault = "System default";

    private static readonly string[] Themes = ["system", "light", "dark"];
    private static readonly string[] LineHeights = ["compact", "comfortable", "spacious"];
    private static readonly string[] ReadingWidths = ["narrow", "wide", "full"];

    public static AppearanceDto Defaults { get; } = new(
        Theme: "system",
        InterfaceSize: 14,
        ReadingSize: 16,
        LineHeight: "comfortable",
        ReadingWidth: "wide",
        InterfaceFont: SystemDefault,
        ReadingFont: SystemDefault,
        MonoFont: SystemDefault);

    public static AppearanceDto Load(ProjectManager manager)
    {
        var d = Defaults;
        return new AppearanceDto(
            Theme: Pick(manager, "appearance.theme", d.Theme, Themes),
            InterfaceSize: Int(manager, "appearance.interfaceSize", d.InterfaceSize),
            ReadingSize: Int(manager, "appearance.readingSize", d.ReadingSize),
            LineHeight: Pick(manager, "appearance.lineHeight", d.LineHeight, LineHeights),
            ReadingWidth: Pick(manager, "appearance.readingWidth", d.ReadingWidth, ReadingWidths),
            InterfaceFont: manager.GetGlobal("appearance.interfaceFont") ?? d.InterfaceFont,
            ReadingFont: manager.GetGlobal("appearance.readingFont") ?? d.ReadingFont,
            MonoFont: manager.GetGlobal("appearance.monoFont") ?? d.MonoFont);
    }

    /// <summary>Checks ranges and font names. A font must be the system default, bundled, or installed here.</summary>
    public static AppearanceDto Validate(AppearanceDto value, IReadOnlyList<string> systemFonts)
    {
        if (!Themes.Contains(value.Theme))
        {
            throw Scope.Invalid("Choose system, light or dark.");
        }

        if (value.InterfaceSize is < 11 or > 20 || value.ReadingSize is < 12 or > 28)
        {
            throw Scope.Invalid("Text sizes must be 11 to 20 for the interface and 12 to 28 for reading.");
        }

        if (!LineHeights.Contains(value.LineHeight) || !ReadingWidths.Contains(value.ReadingWidth))
        {
            throw Scope.Invalid("Choose a known line height and reading width.");
        }

        foreach (var font in new[] { value.InterfaceFont, value.ReadingFont, value.MonoFont })
        {
            if (!IsAllowedFont(font, systemFonts))
            {
                throw Scope.Invalid($"The font '{font}' is not installed on this computer.");
            }
        }

        return value;
    }

    public static void Save(ProjectManager manager, AppearanceDto value)
    {
        manager.SetGlobal("appearance.theme", value.Theme);
        manager.SetGlobal("appearance.interfaceSize", value.InterfaceSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        manager.SetGlobal("appearance.readingSize", value.ReadingSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        manager.SetGlobal("appearance.lineHeight", value.LineHeight);
        manager.SetGlobal("appearance.readingWidth", value.ReadingWidth);
        manager.SetGlobal("appearance.interfaceFont", value.InterfaceFont);
        manager.SetGlobal("appearance.readingFont", value.ReadingFont);
        manager.SetGlobal("appearance.monoFont", value.MonoFont);
    }

    /// <summary>
    /// The window background to paint before the page loads, so a dark theme never flashes white.
    /// These hex values match the <c>--bg</c> token in the frontend.
    /// </summary>
    public static string BackgroundFor(string theme) => theme switch
    {
        "light" => "#FAFAF9",
        "dark" => "#17181A",
        _ => SystemIsDark() ? "#17181A" : "#FAFAF9",
    };

    private static bool SystemIsDark()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private static bool IsAllowedFont(string font, IReadOnlyList<string> systemFonts) =>
        font == SystemDefault
        || BundledFonts.Contains(font, StringComparer.OrdinalIgnoreCase)
        || systemFonts.Contains(font, StringComparer.OrdinalIgnoreCase);

    private static string Pick(ProjectManager manager, string key, string fallback, string[] allowed)
    {
        var value = manager.GetGlobal(key);
        return value is not null && allowed.Contains(value) ? value : fallback;
    }

    private static int Int(ProjectManager manager, string key, int fallback) =>
        int.TryParse(manager.GetGlobal(key), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
