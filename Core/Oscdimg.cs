using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace IsoForge.Core;

public static class Oscdimg
{
    const string ResourceName = "IsoForge.Tools.oscdimg.exe";

    /// <summary>
    /// Devolve o caminho do oscdimg.exe. Primeiro tenta a cópia embutida no
    /// próprio IsoForge (extraída para uma pasta temporária); se não houver,
    /// procura no Windows ADK e no PATH.
    /// </summary>
    public static string? Locate()
    {
        return ExtractBundled() ?? LocateInstalled();
    }

    /// <summary>Extrai o oscdimg.exe embutido no executável, se presente.</summary>
    public static string? ExtractBundled()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName);
        if (stream == null)
            return null;

        var dir = Path.Combine(Path.GetTempPath(), "IsoForge", "oscdimg");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "oscdimg.exe");

        // Reextrai apenas se ausente ou com tamanho diferente
        if (!File.Exists(target) || new FileInfo(target).Length != stream.Length)
        {
            using var file = File.Create(target);
            stream.CopyTo(file);
        }
        return target;
    }

    public static string? LocateInstalled()
    {
        string[] candidates =
        {
            @"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe",
            @"C:\Program Files\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe",
            @"C:\Program Files (x86)\Windows Kits\11\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe",
        };

        foreach (var path in candidates)
            if (File.Exists(path))
                return path;

        try
        {
            var psi = new ProcessStartInfo("where.exe", "oscdimg.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);
                var first = output.Split('\n').FirstOrDefault()?.Trim();
                if (!string.IsNullOrEmpty(first) && File.Exists(first))
                    return first;
            }
        }
        catch { /* where.exe indisponível — ignora */ }

        return null;
    }

    public const string InstallHint =
        "oscdimg.exe não encontrado. Ele normalmente já vem embutido no IsoForge; " +
        "se esta é uma compilação sem o binário, instale o Windows ADK (gratuito da Microsoft) " +
        "marcando apenas \"Ferramentas de Implantação\", ou informe o caminho manualmente.";
}
