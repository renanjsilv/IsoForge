using System.Diagnostics;
using System.IO;
using System.Text;

namespace IsoForge.Core;

/// <summary>
/// Baixa a fonte offline do Office usando o Office Deployment Tool
/// (setup.exe /download), gerando a pasta Office\Data que depois é embutida na ISO.
/// </summary>
public static class OfficeDownloader
{
    public static async Task DownloadAsync(string odtSetupExe, string officeConfigXml, string targetFolder,
        IProgress<string> log, CancellationToken ct, IProgress<string>? headline = null)
    {
        if (!File.Exists(odtSetupExe))
            throw new InvalidOperationException("setup.exe do Office Deployment Tool não encontrado. Adicione o Office na seção 4 primeiro.");

        Directory.CreateDirectory(targetFolder);

        // O config de download aponta SourcePath para a pasta de destino.
        var downloadConfig = OfficeConfig.WithSourcePath(officeConfigXml, targetFolder);
        var configPath = Path.Combine(targetFolder, "DownloadConfig.xml");
        File.WriteAllText(configPath, downloadConfig, new UTF8Encoding(false));

        // Copia o setup.exe para a pasta (necessário para a instalação offline depois).
        File.Copy(odtSetupExe, Path.Combine(targetFolder, "setup.exe"), overwrite: true);

        log.Report($"Baixando Office para {targetFolder} (~3,5 GB, pode demorar)...");

        var psi = new ProcessStartInfo(odtSetupExe, $"/download \"{configPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = targetFolder
        };
        using var p = new Process { StartInfo = psi };
        p.Start();

        // O ODT não reporta progresso; medimos a pasta Office\Data crescendo e mostramos
        // o total baixado (GB) no log e no cabeçalho — sem porcentagem estimada que "prende".
        var officeDir = Path.Combine(targetFolder, "Office");
        double lastLoggedGb = 0;
        long lastSize = 0;
        var lastGrowth = DateTime.UtcNow;
        var stallLimit = TimeSpan.FromMinutes(4); // sem crescer por 4 min => travou
        log.Report("Office: iniciando download...");
        while (!p.HasExited)
        {
            await Task.Delay(2000, ct);
            long size = 0;
            try { if (Directory.Exists(officeDir)) size = new DirectoryInfo(officeDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { /* arquivos em uso durante o download */ }
            var gb = size / 1024.0 / 1024.0 / 1024.0;
            headline?.Report($"{gb:F2} GB baixados");

            if (size > lastSize)
            {
                lastSize = size;
                lastGrowth = DateTime.UtcNow;
                if (gb - lastLoggedGb >= 0.2)   // registra no log a cada ~200 MB
                {
                    log.Report($"  Office: {gb:F2} GB baixados...");
                    lastLoggedGb = gb;
                }
            }
            else if (DateTime.UtcNow - lastGrowth > stallLimit)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException(
                    "O download do Office travou (sem progresso por 4 minutos) e foi cancelado.\n\n" +
                    "O CDN do Office está acessível, mas o Click-to-Run/BITS falhou nesta máquina " +
                    "(veja o log em %TEMP%). Isso costuma ser antivírus/EDR corporativo ou política de BITS.\n\n" +
                    "Alternativas: use o Office no modo ONLINE (baixa em cada máquina), ou rode o download do Office " +
                    "em outra máquina/rede e aponte a pasta em 'Procurar...'.");
            }
        }
        headline?.Report("finalizando...");

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"O download do Office falhou (código {p.ExitCode}). Verifique a internet e o Configuration.xml.");

        var dataDir = Path.Combine(targetFolder, "Office", "Data");
        if (!Directory.Exists(dataDir))
            throw new InvalidOperationException("Download terminou mas a pasta Office\\Data não foi criada. Revise o Configuration.xml.");

        var finalSize = new DirectoryInfo(dataDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        log.Report($"Office baixado: {finalSize / 1024.0 / 1024.0 / 1024.0:F2} GB em {dataDir}");
    }
}
