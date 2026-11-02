using NeoAstra;
using NeoAstra.Rpc;
using Silvia.App.Hosting;
using Silvia.App.Rpc;

[assembly: NeoRpcJsonContext(typeof(AppJsonContext))]

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The data folder and the project manager exist before the window, so a failed recovery
        // stops the app before it shows anything.
        using var host = AppHost.Create();
        var hex = Appearance.BackgroundFor(Appearance.Load(host.Manager).Theme);
        var background = new NeoColor(
            Convert.ToByte(hex[1..3], 16),
            Convert.ToByte(hex[3..5], 16),
            Convert.ToByte(hex[5..7], 16),
            255);

        return NeoApp.Run(args, app =>
        {
            app.Title = "Silvia";
            app.Width = 1280;
            app.Height = 820;
            app.ConfigureMainWindow((application, window) =>
            {
                window.BackgroundColor = background;
                host.FolderPicker.Attach(application, window);
            });
            app.UseRpc(rpc => rpc
                .AddProjectsService(new ProjectsService(host))
                .AddLibraryService(new LibraryService(host))
                .AddChatService(new ChatService(host))
                .AddSettingsService(new SettingsService(host)));
        });
    }
}
