using System.IO;
using Microsoft.Win32;

namespace Paldium.App.Services;

/// <summary>Native file and folder dialogs. Called from UI events, which run on the WPF UI thread.</summary>
public interface IFilePickerService
{
    /// <summary>Returns the chosen folder or null when the dialog was cancelled.</summary>
    string? PickFolder(string title);

    /// <summary>Returns the chosen file or null when cancelled. <paramref name="filter"/> uses the dialog syntax, e.g. "Zip files|*.zip".</summary>
    string? PickFile(string title, string filter);

    /// <summary>Opens a save dialog and returns the chosen path or null when cancelled.</summary>
    string? SaveFile(string title, string filter, string suggestedName);
}

public sealed class WpfFilePickerService : IFilePickerService
{
    private string? _lastSaveDirectory;

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

    public string? SaveFile(string title, string filter, string suggestedName)
    {
        var initialDir = !string.IsNullOrEmpty(_lastSaveDirectory) && Directory.Exists(_lastSaveDirectory)
            ? _lastSaveDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = suggestedName,
            InitialDirectory = initialDir,
            OverwritePrompt = true,
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true) return null;
        var dir = Path.GetDirectoryName(dialog.FileName);
        if (!string.IsNullOrEmpty(dir)) _lastSaveDirectory = dir;
        return dialog.FileName;
    }
}
