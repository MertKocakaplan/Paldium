namespace Paldium.App.Components;

public sealed record CardGridSlot(int Index, bool IsTabStop, Action<int> NotifyFocused);
