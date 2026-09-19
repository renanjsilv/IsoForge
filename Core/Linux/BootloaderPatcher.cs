using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace IsoForge.Core.Linux;

/// <summary>
/// Acrescenta os parâmetros de kernel do IsoForge às entradas de boot da ISO extraída
/// (GRUB para UEFI, isolinux/syslinux para BIOS), para o instalador achar o arquivo de
/// resposta e rodar sozinho. Também encurta o tempo de espera do menu.
/// </summary>
public static class BootloaderPatcher
{
    /// <summary>Resultado da aplicação: quantos arquivos e quantas entradas foram alterados.</summary>
    public record PatchResult(int Files, int Entries)
    {
        public bool Any => Entries > 0;
    }

    // Linhas que carregam o kernel: GRUB (linux/linuxefi) e isolinux (append/kernel).
    static readonly Regex GrubLine = new(@"^(\s*)(linux|linuxefi|linux16)(\s+\S+)(.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex IsolinuxAppend = new(@"^(\s*)(append)(\s+)(.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Aplica os parâmetros em todos os arquivos de configuração de boot encontrados sob
    /// <paramref name="staging"/>.
    /// </summary>
    /// <param name="grubParams">Parâmetros para o GRUB (com ';' escapado).</param>
    /// <param name="isolinuxParams">Parâmetros para o isolinux (sem escape).</param>
    public static PatchResult Apply(string staging, string grubParams, string isolinuxParams, Action<string> log)
    {
        int files = 0, entries = 0;

        foreach (var path in FindConfigs(staging))
        {
            var text = ReadText(path, out var encoding);
            if (text == null) continue;

            int n;
            var patched = KindOf(path) switch
            {
                ConfigKind.Grub => PatchGrub(text, grubParams, out n),
                ConfigKind.SystemdBoot => PatchSystemdBoot(text, isolinuxParams, out n),
                _ => PatchIsolinux(text, isolinuxParams, out n)
            };

            if (n == 0) continue;
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.WriteAllText(path, patched, encoding);
                files++; entries += n;
                log($"  {Path.GetRelativePath(staging, path)}: {n} entrada(s) de boot automatizada(s).");
            }
            catch (Exception ex)
            {
                log($"  Aviso: não consegui gravar {Path.GetRelativePath(staging, path)}: {ex.Message}");
            }
        }

        return new PatchResult(files, entries);
    }

    /// <summary>Arquivos de configuração de bootloader dentro da ISO extraída.</summary>
    public static IEnumerable<string> FindConfigs(string staging)
    {
        var patterns = new[] { "*.cfg", "*.conf" };
        var dirs = new[] { "boot", "EFI", "efi", "isolinux", "syslinux", "loader", "arch" };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Configurações na raiz (loopback.cfg, grub.cfg de algumas distros).
        foreach (var pattern in patterns)
            foreach (var f in SafeFiles(staging, pattern, SearchOption.TopDirectoryOnly))
                if (seen.Add(f)) yield return f;

        foreach (var d in dirs)
        {
            var full = Path.Combine(staging, d);
            if (!Directory.Exists(full)) continue;
            foreach (var pattern in patterns)
                foreach (var f in SafeFiles(full, pattern, SearchOption.AllDirectories))
                    if (seen.Add(f)) yield return f;
        }
    }

    static IEnumerable<string> SafeFiles(string dir, string pattern, SearchOption option)
    {
        string[] result;
        try { result = Directory.GetFiles(dir, pattern, option); }
        catch { yield break; }
        foreach (var f in result) yield return f;
    }

    /// <summary>Formato do arquivo de configuração de boot.</summary>
    public enum ConfigKind { Grub, Isolinux, SystemdBoot }

    /// <summary>Descobre o formato pelo nome/pasta do arquivo.</summary>
    public static ConfigKind KindOf(string path)
    {
        var name = Path.GetFileName(path);
        var dir = Path.GetDirectoryName(path) ?? "";

        // systemd-boot (usado pelo Arch e por ISOs modernas): loader/entries/*.conf
        if (dir.Replace('\\', '/').Contains("loader/entries", StringComparison.OrdinalIgnoreCase)) return ConfigKind.SystemdBoot;
        if (name.Equals("loader.conf", StringComparison.OrdinalIgnoreCase)) return ConfigKind.SystemdBoot;

        if (name.Equals("grub.cfg", StringComparison.OrdinalIgnoreCase)) return ConfigKind.Grub;
        if (name.Equals("loopback.cfg", StringComparison.OrdinalIgnoreCase)) return ConfigKind.Grub;
        if (dir.Contains("grub", StringComparison.OrdinalIgnoreCase)) return ConfigKind.Grub;
        if (dir.Contains("EFI", StringComparison.OrdinalIgnoreCase)) return ConfigKind.Grub;

        return ConfigKind.Isolinux;
    }

    /// <summary>Insere os parâmetros nas entradas do systemd-boot (linhas "options ...").</summary>
    public static string PatchSystemdBoot(string text, string kernelParams, out int entries)
    {
        entries = 0;
        if (string.IsNullOrWhiteSpace(kernelParams)) return text;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        int count = 0;

        foreach (var raw in lines)
        {
            var line = raw;
            var m = Regex.Match(line, @"^(\s*)(options)(\s+)(.*)$", RegexOptions.IgnoreCase);
            if (m.Success && !AlreadyPatched(line, kernelParams))
            {
                line = m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value
                     + InsertParams(m.Groups[4].Value, kernelParams);
                count++;
            }
            else if (Regex.IsMatch(line, @"^\s*timeout\s+\d+\s*$", RegexOptions.IgnoreCase))
            {
                line = "timeout 5"; // segundos, no systemd-boot
            }
            sb.Append(line).Append('\n');
        }

        entries = count;
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    /// <summary>Insere os parâmetros no GRUB e reduz o tempo do menu.</summary>
    public static string PatchGrub(string text, string kernelParams, out int entries)
    {
        entries = 0;
        if (string.IsNullOrWhiteSpace(kernelParams)) return text;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        int count = 0;

        foreach (var raw in lines)
        {
            var line = raw;
            var m = GrubLine.Match(line);
            if (m.Success && !AlreadyPatched(line, kernelParams))
            {
                var head = m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value;
                var tail = m.Groups[4].Value;
                line = head + InsertParams(tail, kernelParams);
                count++;
            }
            else if (Regex.IsMatch(line, @"^\s*set\s+timeout\s*=", RegexOptions.IgnoreCase))
            {
                line = Regex.Replace(line, @"=\s*-?\d+", "=5");
            }
            sb.Append(line).Append('\n');
        }

        entries = count;
        return sb.ToString();
    }

    /// <summary>Insere os parâmetros no isolinux/syslinux e reduz o tempo do menu.</summary>
    public static string PatchIsolinux(string text, string kernelParams, out int entries)
    {
        entries = 0;
        if (string.IsNullOrWhiteSpace(kernelParams)) return text;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        int count = 0;

        foreach (var raw in lines)
        {
            var line = raw;
            var m = IsolinuxAppend.Match(line);
            if (m.Success && !AlreadyPatched(line, kernelParams))
            {
                line = m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value
                     + InsertParams(m.Groups[4].Value, kernelParams);
                count++;
            }
            else if (Regex.IsMatch(line, @"^\s*timeout\s+\d+\s*$", RegexOptions.IgnoreCase))
            {
                line = "timeout 50"; // décimos de segundo
            }
            else if (Regex.IsMatch(line, @"^\s*prompt\s+\d+\s*$", RegexOptions.IgnoreCase))
            {
                line = "prompt 0";
            }
            sb.Append(line).Append('\n');
        }

        entries = count;
        return sb.ToString();
    }

    /// <summary>
    /// Coloca os parâmetros antes do separador "---" quando ele existe (o instalador do
    /// Debian/Ubuntu trata o que vem depois como opções do sistema instalado).
    /// </summary>
    static string InsertParams(string tail, string kernelParams)
    {
        var idx = tail.LastIndexOf(" ---", StringComparison.Ordinal);
        if (idx >= 0)
            return tail[..idx] + " " + kernelParams + tail[idx..];
        return tail.TrimEnd() + " " + kernelParams;
    }

    /// <summary>Evita duplicar os parâmetros se a ISO já tiver sido processada.</summary>
    static bool AlreadyPatched(string line, string kernelParams)
    {
        var first = kernelParams.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first != null && line.Contains(first, StringComparison.OrdinalIgnoreCase);
    }

    static string? ReadText(string path, out Encoding encoding)
    {
        encoding = new UTF8Encoding(false);
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 512 * 1024) return null;  // não é config de boot
            // Arquivo binário disfarçado de .cfg: ignora.
            if (bytes.Take(512).Any(b => b == 0)) return null;
            return new UTF8Encoding(false).GetString(bytes);
        }
        catch { return null; }
    }
}
