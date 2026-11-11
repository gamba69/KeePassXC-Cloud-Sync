using Avalonia.Media;

namespace KeyPassXCCloudSync;

/// <summary>
/// Centralized font choices for the desktop UI. Ordinary controls deliberately
/// inherit Avalonia's theme/system font, including language-aware glyph fallback.
/// Do not bypass Avalonia's text shaping with direct SkiaSharp calls: Avalonia
/// 12.1.x draws the controls, with SkiaSharp 4.154 and HarfBuzzSharp 14.2
/// pinned explicitly by Desktop. For SkiaSharp 4, always pass a typeface
/// when constructing an SKFont (parameterless SKFont uses an empty typeface).
/// </summary>
internal static class UiTypography
{
    internal const int FootnoteSize = 12;
    internal const int CaptionSize = 13;
    internal const int NavigationSize = 12;
    internal const int DiagnosticTextSize = 13;
    internal const int DiagnosticSymbolSize = 15;

    // Named fonts are only used for the fixed-width diagnostic log. No font
    // files are downloaded, embedded or installed. Future Linux/macOS clients
    // can rely on the platform's customary monospace and symbol families.
    internal static FontFamily DiagnosticMonospace => new(DesktopPlatform.Current.DiagnosticMonospaceName);

    // This is used for the rendering compatibility smoke test, and is also
    // the approved path if the desktop gains custom Skia text drawing.
    // Regular Avalonia controls continue using Avalonia's shaping/fallback.
    internal static SkiaSharp.SKFont CreateSkiaFont(float size) =>
        new(SkiaSharp.SKTypeface.Default, size);

    internal static FontFamily DiagnosticSymbols => new(DesktopPlatform.Current.DiagnosticSymbolsName);
}
