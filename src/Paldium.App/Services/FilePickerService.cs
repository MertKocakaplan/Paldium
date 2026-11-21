using Microsoft.Win32;

namespace Paldium.App.Services;

/// <summary>Native file and folder dialogs. Called from UI events, which run on the WPF UI thread.</summary>
public interface IFilePickerService
{
    /// <summary>Returns the chosen folder or null when the dialog was cancelled.</summary>
    string? PickFolder(string title);

    /// <summary>Returns the chosen file or null when cancelled. <paramref name="filter"/> uses the dialog syntax, e.g. "Zip files|*.zip".</summary>
    string? PickFile(string title, string filter);
}

public sealed class WpfFilePickerService : IFilePickerService
{
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, Multiselect = false, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
