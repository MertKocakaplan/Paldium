using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Paldium.App.Resources;
using Paldium.App.Services;

namespace Paldium.App.Components;

/// <summary>
/// Base class for components that show localized text: injects the localizer and re-renders when the
/// UI language changes. Components overriding OnInitialized must call the base implementation.
/// </summary>
public abstract class LocalizedComponentBase : ComponentBase, IDisposable
{
    [Inject] protected LanguageService Language { get; set; } = default!;
    [Inject] protected IStringLocalizer<Strings> L { get; set; } = default!;
    [Inject] protected GameTerms Terms { get; set; } = default!;

    /// <summary>Current UI language code ("en" or "tr"), used for game terms.</summary>
    protected string Lang => Language.Current;

    /// <summary>A game term in the UI language (game text, then resource fallback, then the id).</summary>
    protected string Term(string termId) => Terms.T(termId);

    protected override void OnInitialized()
    {
        Language.Changed += HandleLanguageChanged;
    }

    private void HandleLanguageChanged(object? sender, EventArgs e) => InvokeAsync(() =>
    {
        OnUiLanguageChanged();
        StateHasChanged();
    });

    /// <summary>Called on the render thread when the UI language changed, before the component renders again (re-sort, re-format).</summary>
    protected virtual void OnUiLanguageChanged()
    {
    }

    public virtual void Dispose()
    {
        Language.Changed -= HandleLanguageChanged;
        GC.SuppressFinalize(this);
    }
}
