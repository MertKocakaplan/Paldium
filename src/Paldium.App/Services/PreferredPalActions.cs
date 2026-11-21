using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MudBlazor;
using Paldium.App.Resources;
using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.App.Services;

/// <summary>"Add to preferred" / "Remove from preferred" from My Pals: toggles the Pal and tells the user, with a way to open the Planner.</summary>
public sealed class PreferredPalActions(PreferredPals preferred, ISnackbar snackbar, NavigationManager navigation, IStringLocalizer<Strings> l)
{
    public bool IsPreferred(OwnedPal pal) => preferred.IsPreferred(pal);

    public void Toggle(OwnedPal pal)
    {
        var now = preferred.Toggle(pal);
        snackbar.Add(
            now ? l["MyPals.PreferredAdded"].Value : l["MyPals.PreferredRemoved"].Value,
            Severity.Normal,
            config =>
            {
                config.Action = l["MyPals.OpenInPlanner"].Value;
                config.OnClick = _ =>
                {
                    navigation.NavigateTo("planner");
                    return Task.CompletedTask;
                };
            });
    }
}
