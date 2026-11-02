using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Dialogs;

namespace Silvia.App.Hosting;

/// <summary>
/// Opens the native folder dialog from the backend, owned by the main window. The dialog runs as a
/// backend API, so the page needs no desktop capability: it only learns the folder the person chose.
/// </summary>
public sealed class FolderPicker
{
    private INeoDialogs? _dialogs;
    private NeoWindow? _owner;

    /// <summary>Binds the picker to the application's UI dispatcher and its main window. Called once at startup.</summary>
    public void Attach(NeoApplication application, NeoWindow owner)
    {
        _dialogs = NeoDialogs.CreateSystem(application.Dispatcher);
        _owner = owner;
    }

    /// <summary>The chosen folder with <c>picked</c>, or <c>canceled</c>, or <c>unavailable</c> when no dialog can be shown.</summary>
    public async ValueTask<(string Status, string? Path)> PickAsync(string title, CancellationToken cancellationToken)
    {
        if (_dialogs is null)
        {
            return ("unavailable", null);
        }

        // Any folder on any drive may be indexed, so every ready drive is a root. The scope still rejects
        // device and other non-drive paths before a result reaches the page.
        var roots = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName);
        var result = await _dialogs.OpenFoldersAsync(
            new NeoFileDialogOptions { Owner = _owner, Title = title, AllowMultiple = false, Scope = new NeoFileScope(roots) },
            cancellationToken);

        return result.Status switch
        {
            NeoDesktopStatus.Success when result.Value is [var path, ..] => ("picked", path),
            NeoDesktopStatus.Success or NeoDesktopStatus.Canceled => ("canceled", null),
            _ => ("unavailable", null),
        };
    }
}
