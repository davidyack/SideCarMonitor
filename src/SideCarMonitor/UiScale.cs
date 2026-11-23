using Microsoft.UI.Xaml;

namespace SideCarMonitor;

/// <summary>
/// The kiosk is a tiny high-density touchscreen running at 100% Windows scaling, so the XAML is written
/// in "design units" and every size comes from these resources, multiplied by display.uiScale.
/// Must run before any window is created, because the XAML uses StaticResource lookups.
/// </summary>
public static class UiScale
{
    public static void Apply(ResourceDictionary r, double scale)
    {
        scale = Math.Clamp(scale, 0.5, 6);
        double S(double v) => Math.Round(v * scale);

        // Text
        r["FontCaption"] = S(11);
        r["FontBody"] = S(14);
        r["FontTitle"] = S(18);
        r["FontClock"] = S(30);
        r["FontIcon"] = S(20);
        r["FontTileIcon"] = S(40);
        r["FontTileValue"] = S(26);
        r["FontBig"] = S(34);
        r["FontHero"] = S(60);

        // Touch targets and spacing
        r["TouchTarget"] = S(48);          // ~9 mm on the kiosk at 2.5x
        r["MenuButtonWidth"] = S(220);
        r["Gap"] = S(6);
        r["SmallGap"] = S(3);
        r["Dot"] = S(7);
        r["Spinner"] = S(28);
        r["PagePadding"] = new Thickness(S(8));
        r["PanelPadding"] = new Thickness(S(10), S(8), S(10), S(8));
        r["RowPadding"] = new Thickness(S(4), S(6), S(4), S(6));
        r["LabelPadding"] = new Thickness(S(7), S(3), S(7), S(3));
        r["LabelMargin"] = new Thickness(S(5));
        r["TileBorder"] = new Thickness(Math.Max(2, S(1.5)));
        r["TilePadding"] = new Thickness(S(12), S(10), S(12), S(10));
        r["Corner"] = new CornerRadius(S(6));
        r["SmallCorner"] = new CornerRadius(S(4));
        r["ActivityWidth"] = new GridLength(S(210));
        r["ActivityRowHeight"] = S(52);
        r["ActivityThumbHeight"] = S(48);
        r["ActivityThumbWidth"] = S(85);

        // Voice assistant: floating mic button and side panel
        r["FabSize"] = S(64);
        r["FabCorner"] = new CornerRadius(S(32));
        r["FabMargin"] = new Thickness(0, 0, S(12), S(12));
        r["AssistantWidth"] = S(440);
        r["QrSize"] = S(300);
    }
}
