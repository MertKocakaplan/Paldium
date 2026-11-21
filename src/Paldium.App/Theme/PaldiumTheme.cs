using MudBlazor;

namespace Paldium.App.Theme;

/// <summary>
/// Minimal MudBlazor theme configuration (COMPONENTS.md 1.1): only typography and layout metrics remain;
/// all colors live in <c>wwwroot/css/app.css</c> under <c>:root</c>.
/// </summary>
public static class PaldiumTheme
{
    private static readonly string[] Sans = ["'IBM Plex Sans'", "'Segoe UI Variable Text'", "sans-serif"];
    private static readonly string[] Mono = ["'IBM Plex Mono'", "ui-monospace", "monospace"];

    public static readonly MudTheme Value = new()
    {
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Sans, FontSize = "14px", FontWeight = "400", LineHeight = "20px", LetterSpacing = "0", TextTransform = "none" },
            H1 = new H1Typography { FontFamily = Sans, FontSize = "28px", FontWeight = "600", LineHeight = "34px", LetterSpacing = "-0.01em", TextTransform = "none" },
            H2 = new H2Typography { FontFamily = Sans, FontSize = "18px", FontWeight = "600", LineHeight = "24px", LetterSpacing = "0", TextTransform = "none" },
            H3 = new H3Typography { FontFamily = Sans, FontSize = "15px", FontWeight = "600", LineHeight = "20px", LetterSpacing = "0", TextTransform = "none" },
            H4 = new H4Typography { FontFamily = Sans, FontSize = "28px", FontWeight = "600", LineHeight = "34px", LetterSpacing = "-0.01em", TextTransform = "none" },
            H5 = new H5Typography { FontFamily = Mono, FontSize = "28px", FontWeight = "500", LineHeight = "32px", LetterSpacing = "0", TextTransform = "none" },
            H6 = new H6Typography { FontFamily = Sans, FontSize = "15px", FontWeight = "600", LineHeight = "20px", LetterSpacing = "0", TextTransform = "none" },
            Subtitle1 = new Subtitle1Typography { FontFamily = Sans, FontSize = "14px", FontWeight = "600", LineHeight = "20px", LetterSpacing = "0", TextTransform = "none" },
            Subtitle2 = new Subtitle2Typography { FontFamily = Sans, FontSize = "13.5px", FontWeight = "500", LineHeight = "20px", LetterSpacing = "0", TextTransform = "none" },
            Body1 = new Body1Typography { FontFamily = Sans, FontSize = "14px", FontWeight = "400", LineHeight = "20px", LetterSpacing = "0", TextTransform = "none" },
            Body2 = new Body2Typography { FontFamily = Sans, FontSize = "12.5px", FontWeight = "400", LineHeight = "18px", LetterSpacing = "0", TextTransform = "none" },
            Button = new ButtonTypography { FontFamily = Sans, FontSize = "13px", FontWeight = "500", LineHeight = "20px", LetterSpacing = "0", TextTransform = "none" },
            Caption = new CaptionTypography { FontFamily = Sans, FontSize = "12px", FontWeight = "400", LineHeight = "16px", LetterSpacing = "0", TextTransform = "none" },
            Overline = new OverlineTypography { FontFamily = Sans, FontSize = "12px", FontWeight = "600", LineHeight = "16px", LetterSpacing = "0", TextTransform = "none" },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "6px",
            AppbarHeight = "44px",
            DrawerWidthRight = "360px",
        },
    };
}
