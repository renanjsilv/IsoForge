using System.Windows;
using IsoForge.Core;

namespace IsoForge;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Modo headless usado pelo instalador para pré-baixar os instaladores após instalar.
        if (e.Args.Any(a => a.Equals("--fetch", StringComparison.OrdinalIgnoreCase)))
        {
            var fetcher = new InstallerFetcher();
            var log = new Progress<string>(_ => { });
            foreach (var id in new[] { AppId.SevenZip, AppId.AnyDesk, AppId.OfficeOdt })
            {
                try { await fetcher.EnsureAsync(id, log, CancellationToken.None); } catch { /* melhor esforço */ }
            }
            Shutdown(0);
            return;
        }

        new MainWindow().Show();
    }
}
