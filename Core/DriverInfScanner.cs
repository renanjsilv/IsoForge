using System.IO;

namespace IsoForge.Core;

/// <summary>Uma categoria de drivers (agrupada pela classe dos .inf) para seleção pelo usuário.</summary>
public record DriverCategory(string Name, int Count, long SizeBytes)
{
    public string SizeText => SizeBytes >= 1024L * 1024 ? $"{SizeBytes / 1024.0 / 1024.0:0} MB" : $"{SizeBytes / 1024.0:0} KB";
}

/// <summary>
/// Lê os .inf de um driver pack extraído, agrupa por categoria (a partir da classe do driver)
/// e copia só as categorias escolhidas — permite instalar drivers específicos, não o pack inteiro.
/// </summary>
public static class DriverInfScanner
{
    /// <summary>Traduz a classe do .inf (Class=) para uma categoria amigável em PT.</summary>
    public static string Categorize(string? infClass) => (infClass ?? "").Trim().ToLowerInvariant() switch
    {
        "net" => "Rede",
        "display" => "Vídeo",
        "media" => "Áudio",
        "system" => "Chipset / Sistema",
        "hdc" or "scsiadapter" or "diskdrive" => "Armazenamento",
        "bluetooth" => "Bluetooth",
        "usb" or "usbdevice" => "USB",
        "hidclass" or "keyboard" or "mouse" => "Entrada (teclado/mouse)",
        "firmware" => "Firmware",
        "camera" or "image" => "Câmera",
        "biometric" => "Biometria",
        "smartcardreader" => "Leitor de cartão",
        "monitor" => "Monitor",
        "printer" => "Impressora",
        "securitydevices" or "encryption" => "Segurança (TPM)",
        _ => "Outros"
    };

    /// <summary>Lê o valor de Class= no .inf (ignora ClassGuid= e comentários).</summary>
    static string ReadClass(string infPath)
    {
        try
        {
            foreach (var raw in File.ReadLines(infPath))
            {
                var line = raw.Trim();
                if (line.StartsWith("Class", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("ClassGuid", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("ClassVer", StringComparison.OrdinalIgnoreCase))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var val = line[(i + 1)..].Trim();
                    var sc = val.IndexOf(';'); if (sc >= 0) val = val[..sc].Trim();
                    if (val.Length > 0) return val;
                }
            }
        }
        catch { }
        return "";
    }

    /// <summary>Enumera as categorias de drivers presentes no pack extraído (com contagem e tamanho).</summary>
    public static List<DriverCategory> Scan(string root)
    {
        var byCat = new Dictionary<string, (int count, long size)>();
        if (!Directory.Exists(root)) return new();
        foreach (var inf in Directory.EnumerateFiles(root, "*.inf", SearchOption.AllDirectories))
        {
            var cat = Categorize(ReadClass(inf));
            long size = 0;
            try
            {
                var dir = Path.GetDirectoryName(inf)!;
                size = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch { }
            var cur = byCat.TryGetValue(cat, out var v) ? v : (count: 0, size: 0L);
            byCat[cat] = (cur.count + 1, cur.size + size);
        }
        return byCat
            .Select(kv => new DriverCategory(kv.Key, kv.Value.count, kv.Value.size))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Copia para <paramref name="dest"/> só os drivers cujas categorias NÃO estão em
    /// <paramref name="excludedCategories"/> (vazio = copia todos). Preserva o caminho relativo.
    /// Devolve o total de bytes copiados.
    /// </summary>
    public static long CopySelected(string root, ISet<string> excludedCategories, string dest)
    {
        long bytes = 0;
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var inf in Directory.EnumerateFiles(root, "*.inf", SearchOption.AllDirectories))
        {
            var cat = Categorize(ReadClass(inf));
            if (excludedCategories.Contains(cat)) continue;
            var folder = Path.GetDirectoryName(inf)!;
            if (!done.Add(folder)) continue; // já copiou esta pasta (pode ter vários .inf)
            var rel = Path.GetRelativePath(root, folder);
            var target = Path.Combine(dest, rel);
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var frel = Path.GetRelativePath(folder, f);
                var fdst = Path.Combine(target, frel);
                Directory.CreateDirectory(Path.GetDirectoryName(fdst)!);
                File.Copy(f, fdst, true);
                bytes += new FileInfo(f).Length;
            }
        }
        return bytes;
    }
}
