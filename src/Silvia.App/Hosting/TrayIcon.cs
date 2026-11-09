using System.Reflection;
using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Menus;
using NeoAstra.Desktop.Tray;

namespace Silvia.App.Hosting;

/// <summary>
/// Silvia's icon in the notification area. A click on it brings the window back; its menu opens the window or quits.
/// When <c>closeToTray</c> says so, closing the window hides it instead, and the app keeps indexing and refreshing
/// in the background until <b>Quit Silvia</b> is chosen. Where the platform has no tray, nothing changes: closing the
/// window quits as before.
/// </summary>
public sealed class TrayIcon(Func<bool> closeToTray)
{
    private const string ItemId = "silvia";
    private const string OpenCommand = "silvia.tray.open";
    private const string QuitCommand = "silvia.tray.quit";

    private NeoDesktopServices? _desktop;
    private NeoApplication? _application;
    private NeoWindow? _window;
    private volatile bool _quitting;

    /// <summary>True once the icon is in the notification area.</summary>
    public bool IsShown { get; private set; }

    /// <summary>The icon shipped next to the executable.</summary>
    public static string DefaultIconPath => Path.Combine(AppContext.BaseDirectory, "Resources", "silvia.ico");

    /// <summary>
    /// Shows the icon and takes over the main window's close button when closing to the tray is on. Called once at
    /// startup, on the UI thread. A failure leaves the app without a tray icon, never without its window.
    /// </summary>
    public void Attach(NeoApplication application, NeoWindow window, string dataRoot, string? iconPath = null)
    {
        _application = application;
        _window = window;
        iconPath ??= DefaultIconPath;
        if (!File.Exists(iconPath))
        {
            return;
        }

        try
        {
            var privateData = Path.Combine(dataRoot, "desktop");
            Directory.CreateDirectory(privateData);
            _desktop = NeoDesktopServices.CreateSystem(
                "app.silvia", "Silvia", Version, privateData, [], [], [], [], application.Dispatcher);
            if (!_desktop.Tray.Support.IsSupported)
            {
                return;
            }

            _desktop.Menus.Commands.Register(OpenCommand, _ =>
            {
                Show();
                return ValueTask.CompletedTask;
            });
            _desktop.Menus.Commands.Register(QuitCommand, _ =>
            {
                Quit();
                return ValueTask.CompletedTask;
            });
            _desktop.Tray.Activated += (_, activation) =>
            {
                if (!activation.Secondary)
                {
                    Show();
                }
            };
            _desktop.Tray.Set(new NeoTrayItemOptions
            {
                Id = ItemId,
                ToolTip = "Silvia",
                IconPath = iconPath,
                Menu =
                [
                    NeoMenuItem.Command("open", "Open Silvia", OpenCommand),
                    NeoMenuItem.Separator("separator"),
                    NeoMenuItem.Command("quit", "Quit Silvia", QuitCommand),
                ],
            });
            IsShown = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Silvia: no tray icon ({ex.Message}).");
            return;
        }

        window.CloseRequested += request =>
        {
            // Only the person's own close is kept from quitting; a sign-out or an app quit closes as always.
            if (!_quitting && IsShown && request.Reason == NeoWindowCloseReason.User && request.CanCancel && closeToTray())
            {
                request.Cancel();
                window.Hide();
            }

            return ValueTask.CompletedTask;
        };

        // The icon goes with the app, not when Windows notices the process is gone.
        application.Stopping += (_, _) =>
        {
            try
            {
                _desktop?.Tray.Remove(ItemId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Exiting anyway.
            }
        };
    }

    /// <summary>Brings the main window back, restored and in front.</summary>
    public void Show() => OnUiThread(window =>
    {
        window.Show();
        if (window.State == NeoWindowState.Minimized)
        {
            window.Restore();
        }

        window.BringToFront();
    });

    /// <summary>Closes the main window for good, which ends the app.</summary>
    public void Quit()
    {
        _quitting = true;
        OnUiThread(window => window.Close());
    }

    private void OnUiThread(Action<NeoWindow> action)
    {
        if (_window is not { IsClosed: false } window || _application is null)
        {
            return;
        }

        if (_application.Dispatcher.CheckAccess())
        {
            action(window);
        }
        else
        {
            _application.Dispatcher.Post(() => action(window));
        }
    }

    private static string Version =>
        typeof(TrayIcon).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
}
