using System.IO;
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

        // Diagnóstico: baixa o catálogo Dell e grava contagem + amostra num arquivo (validação do parser).
        if (e.Args.Any(a => a.Equals("--dumpdrivers", StringComparison.OrdinalIgnoreCase)))
        {
            var outFile = Path.Combine(Path.GetTempPath(), "isoforge_drivers.txt");
            try
            {
                var cat = new DellDriverCatalog();
                var models = await cat.FetchModelsAsync(new Progress<string>(_ => { }), CancellationToken.None);
                var sample = string.Join("\n", models.Take(12).Select(m => $"  {m.Label}  [{m.SizeText}]"));
                File.WriteAllText(outFile, $"OK modelos={models.Count}\n{sample}\n");
            }
            catch (Exception ex) { File.WriteAllText(outFile, $"ERRO: {ex}"); }
            Shutdown(0);
            return;
        }

        // Diagnóstico: valida o catálogo de componentes individuais (parse real + drivers de um modelo).
        if (e.Args.Any(a => a.Equals("--dumpindiv", StringComparison.OrdinalIgnoreCase)))
        {
            var outFile = Path.Combine(Path.GetTempPath(), "isoforge_indiv.txt");
            try
            {
                var cat = new DellComponentCatalog();
                await cat.EnsureLoadedAsync(new Progress<string>(_ => { }), CancellationToken.None);
                var models = cat.Models();
                var pick = models.FirstOrDefault(m => m.Name.Contains("Latitude 54", StringComparison.OrdinalIgnoreCase)) ?? models.FirstOrDefault();
                var drivers = pick != null ? cat.DriversForName(pick.Name) : new();
                var sample = string.Join("\n", drivers.Take(12).Select(d => $"  [{d.Category}] {d.Name} — {d.SizeText}"));
                File.WriteAllText(outFile, $"OK modelos={models.Count}\nmodelo={pick?.Name} systemID={pick?.SystemId} drivers={drivers.Count}\n{sample}\n");
            }
            catch (Exception ex) { File.WriteAllText(outFile, $"ERRO: {ex}"); }
            Shutdown(0);
            return;
        }

        // Diagnóstico: baixa 1 DUP conhecido e testa a extração (/drivers= , /e=) com o código atual.
        if (e.Args.Any(a => a.Equals("--testextract", StringComparison.OrdinalIgnoreCase)))
        {
            var outFile = Path.Combine(Path.GetTempPath(), "isoforge_extract.txt");
            var lines = new List<string>();
            try
            {
                var cat = new DellComponentCatalog();
                var drv = new DriverComponent(
                    "Intel Integrated Sensor Solution Driver",
                    "Chipset / Sistema",
                    "https://downloads.dell.com/FOLDER12660163M/4/Intel-Integrated-Sensor-Solution-Driver_025D7_WIN64_3.11.100.7733_A00_02.EXE",
                    "6ca1a71bbaa8a53f3509858d3b39033f",
                    11003752);
                var folder = await cat.DownloadAndExtractAsync(new[] { drv }, "TesteExtract",
                    new Progress<string>(s => lines.Add(s)), null, CancellationToken.None);
                var infs = System.IO.Directory.GetFiles(folder, "*.inf", System.IO.SearchOption.AllDirectories);
                lines.Add($"RESULTADO: pasta={folder} inf={infs.Length}");
                foreach (var f in infs.Take(8)) lines.Add("  " + f.Substring(folder.Length));
            }
            catch (Exception ex) { lines.Add($"ERRO: {ex.Message}"); }
            File.WriteAllLines(outFile, lines);
            Shutdown(0);
            return;
        }

        // Diagnóstico: valida o catálogo de packs da Lenovo (parse real + amostra).
        if (e.Args.Any(a => a.Equals("--dumplenovo", StringComparison.OrdinalIgnoreCase)))
        {
            var outFile = Path.Combine(Path.GetTempPath(), "isoforge_lenovo.txt");
            try
            {
                var cat = new LenovoDriverCatalog();
                var models = await cat.FetchModelsAsync(new Progress<string>(_ => { }), CancellationToken.None);
                var sample = string.Join("\n", models.Take(10).Select(m => $"  {m.Label}\n    {m.Url}"));
                File.WriteAllText(outFile, $"OK modelos={models.Count}\n{sample}\n");
            }
            catch (Exception ex) { File.WriteAllText(outFile, $"ERRO: {ex}"); }
            Shutdown(0);
            return;
        }

        // Diagnóstico: valida o catálogo INDIVIDUAL da Lenovo (MTM -> descritores).
        if (e.Args.Any(a => a.Equals("--dumplenovoindiv", StringComparison.OrdinalIgnoreCase)))
        {
            var outFile = Path.Combine(Path.GetTempPath(), "isoforge_lenovoindiv.txt");
            try
            {
                var cat = new LenovoComponentCatalog();
                await cat.EnsureLoadedAsync(new Progress<string>(_ => { }), CancellationToken.None);
                var models = cat.Models();
                var pick = models.FirstOrDefault(m => m.Name.Contains("ThinkPad", StringComparison.OrdinalIgnoreCase)) ?? models.FirstOrDefault();
                var drivers = pick != null ? await cat.DriversForModelAsync(pick, new Progress<string>(_ => { }), CancellationToken.None) : new();
                var sample = string.Join("\n", drivers.Take(15).Select(d => $"  [{d.Category}] {d.Name} — {d.SizeText}"));
                File.WriteAllText(outFile, $"OK modelos={models.Count}\nmodelo={pick?.Name} MTM={pick?.SystemId} drivers={drivers.Count}\n{sample}\n");
            }
            catch (Exception ex) { File.WriteAllText(outFile, $"ERRO: {ex}"); }
            Shutdown(0);
            return;
        }

        new MainWindow().Show();
    }
}
