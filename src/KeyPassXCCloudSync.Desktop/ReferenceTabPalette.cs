using Avalonia.Media;

namespace KeyPassXCCloudSync;

// Exact tab colors from the user-supplied KeePassXCCardPrint source:
// App.axaml (KpWindowBrush, KpButtonBrush, KpSurfaceBrush, KpSelectedBrush,
// KpTextBrush, KpBorderBrush) and Styles/Compact.axaml (TabItem states).
// Cloud Sync now uses native TabItem controls, just like the source project.
// The supplied source styles are in Styles/CardPrintTabs.axaml, and the
// corresponding brushes are in App.axaml. This helper is only used for
// navigation-strip background and verification of the reference palette.
internal static class ReferenceTabPalette
{
    internal readonly record struct Colors(
        Color Window, Color Button, Color Surface, Color Hover,
        Color Text, Color Border);

    internal static Colors For(bool dark) => dark
        ? new(
            Color.Parse("#081524"), // KpWindowBrush
            Color.Parse("#20384e"), // KpButtonBrush
            Color.Parse("#182b3f"), // KpSurfaceBrush (selected TabItem)
            Color.Parse("#243b56"), // KpSelectedBrush (pointerover)
            Color.Parse("#eef4fb"), // KpTextBrush
            Color.Parse("#31475f")) // KpBorderBrush
        : new(
            Color.Parse("#f3f4f7"), // KpWindowBrush
            Color.Parse("#e8edf4"), // KpButtonBrush
            Color.Parse("#ffffff"), // KpSurfaceBrush (selected TabItem)
            Color.Parse("#dbeafe"), // KpSelectedBrush (pointerover)
            Color.Parse("#1c2730"), // KpTextBrush
            Color.Parse("#ced4dc")); // KpBorderBrush
}
