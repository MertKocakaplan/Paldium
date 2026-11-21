using Microsoft.Web.WebView2.Core;

namespace Paldium.App.Services;

/// <summary>Detects the WebView2 runtime that the Blazor window needs.</summary>
public static class WebView2Check
{
    public const string DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>Installed runtime version, or null when no runtime is present.</summary>
    public static string? InstalledVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
