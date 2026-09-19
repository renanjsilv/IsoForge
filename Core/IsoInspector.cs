using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using IsoForge.Models;

namespace IsoForge.Core;

/// <summary>Resultado da identificação de uma ISO.</summary>
/// <param name="Os">Sistema reconhecido, ou null se não foi possível identificar.</param>
/// <param name="Label">Rótulo do volume (Volume Identifier do ISO 9660).</param>
/// <param name="Description">Descrição legível (ex.: "Ubuntu 24.04.1").</param>
/// <param name="Version">Versão detectada no rótulo, quando existir.</param>
/// <param name="Supported">O sistema reconhecido é personalizável pelo IsoForge?</param>
/// <param name="UnsupportedName">Nome do sistema reconhecido mas não suportado (ex.: "Pop!_OS").</param>
public record IsoIdentity(
    TargetOs? Os,
    string Label,
    string Description,
    string Version = "",
    bool Supported = true,
    string UnsupportedName = "");

/// <summary>
/// Identifica qual sistema operacional está dentro de um arquivo .iso lendo diretamente as
/// estruturas ISO 9660 (descritor primário de volume + diretório raiz) — sem montar a imagem.
/// Usado para corrigir automaticamente o sistema selecionado quando o usuário escolhe uma ISO
/// que não corresponde ao SO ativo.
/// </summary>
public static class IsoInspector
{
    const int SectorSize = 2048;
    const long PvdOffset = 16 * SectorSize; // descritor primário de volume

    // Distros reconhecidas pelo rótulo mas que o IsoForge ainda não personaliza.
    static readonly (string Pattern, string Name)[] KnownUnsupported =
    {
        (@"^POP_?OS", "Pop!_OS"),
        (@"^MANJARO", "Manjaro"),
        (@"^KALI", "Kali Linux"),
        (@"^ZORIN", "Zorin OS"),
        (@"^ELEMENTARY", "elementary OS"),
        (@"^ENDEAVOUR", "EndeavourOS"),
        (@"^GARUDA", "Garuda Linux"),
        (@"^NIXOS", "NixOS"),
        (@"^ALPINE", "Alpine Linux"),
        (@"^CENT[_-]?OS", "CentOS"),
        (@"^PROXMOX|^PVE", "Proxmox VE"),
        (@"^FREEBSD", "FreeBSD"),
    };

    // Rótulo de volume -> sistema. A ordem importa: padrões mais específicos primeiro.
    static readonly (string Pattern, TargetOs Os)[] LabelSignatures =
    {
        (@"UBUNTU[-_ ]?SERVER", TargetOs.UbuntuServer),
        (@"^UBUNTU", TargetOs.Ubuntu),
        (@"^LINUX[ _]?MINT|^LMDE", TargetOs.LinuxMint),
        (@"^DEBIAN", TargetOs.Debian),
        (@"^FEDORA", TargetOs.Fedora),
        (@"^ROCKY", TargetOs.RockyLinux),
        (@"^ALMA", TargetOs.AlmaLinux),
        (@"^OPENSUSE|^SUSE|^SLE[-_]", TargetOs.OpenSuse),
        (@"^ARCH", TargetOs.ArchLinux),
    };

    /// <summary>
    /// Lê o arquivo .iso e devolve o sistema identificado. Nunca lança: em caso de erro de
    /// leitura devolve null.
    /// </summary>
    public static IsoIdentity? Identify(string isoPath)
    {
        if (string.IsNullOrWhiteSpace(isoPath) || !File.Exists(isoPath)) return null;

        string label = "", application = "";
        List<string> rootEntries = new();
        try
        {
            using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < PvdOffset + SectorSize) return null;

            var pvd = ReadSector(fs, PvdOffset);
            // Tipo 1 = descritor primário; assinatura "CD001".
            if (pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") return null;

            label = Ascii(pvd, 40, 32);
            application = Ascii(pvd, 574, 128);
            rootEntries = ReadRootEntries(fs, pvd);
        }
        catch { return null; }

        var os = FromLabel(label) ?? FromLabel(application) ?? FromRootEntries(rootEntries, label)
                 ?? FromFileName(Path.GetFileName(isoPath));

        var unsupported = MatchUnsupported(label) ?? MatchUnsupported(application) ?? MatchUnsupported(Path.GetFileName(isoPath));
        if (os == null && unsupported != null)
            return new IsoIdentity(null, label, unsupported, VersionIn(label), Supported: false, UnsupportedName: unsupported);

        if (os == null)
            return new IsoIdentity(null, label, string.IsNullOrWhiteSpace(label) ? "(sem rótulo)" : label);

        var version = VersionIn(label) is { Length: > 0 } v ? v : VersionIn(Path.GetFileName(isoPath));
        var name = OsCatalog.NameOf(os.Value);
        var desc = string.IsNullOrWhiteSpace(version) ? name : $"{name} {version}";
        return new IsoIdentity(os, label, desc, version);
    }

    /// <summary>
    /// Compara o sistema selecionado com o conteúdo real da ISO. Devolve o sistema correto
    /// quando há divergência (para correção automática), ou null quando batem / não deu para saber.
    /// </summary>
    public static TargetOs? Mismatch(TargetOs selected, IsoIdentity? id)
    {
        if (id?.Os is not { } actual) return null;
        if (actual == selected) return null;

        // Ubuntu Desktop x Server: variantes do mesmo instalador (autoinstall). O rótulo nem
        // sempre distingue, então só corrige se a ISO se identificou explicitamente como Server.
        if (selected == TargetOs.Ubuntu && actual == TargetOs.UbuntuServer &&
            !id.Label.Contains("SERVER", StringComparison.OrdinalIgnoreCase))
            return null;

        // Windows 10 e 11 usam o MESMO rótulo oficial (CCCOMA_X64FRE_...). Sem uma marca
        // explícita da versão, a escolha do usuário prevalece — trocar seria um chute.
        if (OsCatalog.IsWindows(selected) && OsCatalog.IsWindows(actual) && !SaysWindowsVersion(id.Label))
            return null;

        return actual;
    }

    /// <summary>O rótulo diz explicitamente qual versão do Windows está na imagem?</summary>
    static bool SaysWindowsVersion(string label) =>
        !string.IsNullOrWhiteSpace(label) &&
        Regex.IsMatch(label.ToUpperInvariant(), @"WIN(DOWS)?[ _-]?1[01]|19041|19045");

    // ------------------------------------------------------------------
    // Heurísticas
    // ------------------------------------------------------------------
    static TargetOs? FromLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var up = label.Trim().ToUpperInvariant();

        // Windows: rótulos oficiais são do tipo CCCOMA_X64FRE_PT-BR_DV9 / CPBA_X64FRE_...
        if (Regex.IsMatch(up, @"X64FRE|X86FRE|A64FRE|^(CCCOMA|CPBA|CENA|CCSA|SSS_|IR\d)"))
            return Regex.IsMatch(up, @"WIN10|_10_|19041|19045") ? TargetOs.Windows10 : TargetOs.Windows11;
        if (Regex.IsMatch(up, @"^WIN(DOWS)?[ _-]?11")) return TargetOs.Windows11;
        if (Regex.IsMatch(up, @"^WIN(DOWS)?[ _-]?10")) return TargetOs.Windows10;

        foreach (var (pattern, os) in LabelSignatures)
            if (Regex.IsMatch(up, pattern)) return os;

        return null;
    }

    static string? MatchUnsupported(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var up = text.Trim().ToUpperInvariant();
        foreach (var (pattern, name) in KnownUnsupported)
            if (Regex.IsMatch(up, pattern)) return name;
        return null;
    }

    /// <summary>Identifica pela estrutura do diretório raiz quando o rótulo não diz nada.</summary>
    static TargetOs? FromRootEntries(List<string> entries, string label)
    {
        if (entries.Count == 0) return null;
        var set = new HashSet<string>(entries, StringComparer.OrdinalIgnoreCase);
        bool Has(params string[] names) => names.Any(set.Contains);

        // Windows: sources\install.wim + setup.exe na raiz.
        if (Has("SOURCES") && Has("SETUP.EXE")) return TargetOs.Windows11;
        // Ubuntu e derivados live: casper/
        if (Has("CASPER"))
            return label.Contains("SERVER", StringComparison.OrdinalIgnoreCase) ? TargetOs.UbuntuServer : TargetOs.Ubuntu;
        // Debian netinst/DVD: install.amd + dists/pool
        if (Has("INSTALL.AMD", "INSTALL.386") && Has("DISTS")) return TargetOs.Debian;
        // Anaconda (Fedora/Rocky/Alma): images/ + LiveOS/ ou .treeinfo
        if (Has("IMAGES") && Has("LIVEOS", ".TREEINFO", "TREEINFO")) return TargetOs.Fedora;
        // openSUSE: suse/ ou content na raiz
        if (Has("SUSE", "CONTENT")) return TargetOs.OpenSuse;
        // Arch: arch/ na raiz
        if (Has("ARCH")) return TargetOs.ArchLinux;

        return null;
    }

    static TargetOs? FromFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var up = fileName.ToUpperInvariant();
        if (up.Contains("UBUNTU") && up.Contains("SERVER")) return TargetOs.UbuntuServer;
        if (up.Contains("UBUNTU")) return TargetOs.Ubuntu;
        if (up.Contains("LINUXMINT") || up.Contains("LINUX-MINT") || up.Contains("LMDE")) return TargetOs.LinuxMint;
        if (up.Contains("DEBIAN")) return TargetOs.Debian;
        if (up.Contains("FEDORA")) return TargetOs.Fedora;
        if (up.Contains("ROCKY")) return TargetOs.RockyLinux;
        if (up.Contains("ALMALINUX")) return TargetOs.AlmaLinux;
        if (up.Contains("OPENSUSE")) return TargetOs.OpenSuse;
        if (up.Contains("ARCHLINUX") || up.StartsWith("ARCH-")) return TargetOs.ArchLinux;
        if (up.Contains("WIN11") || up.Contains("WINDOWS11") || up.Contains("WINDOWS_11")) return TargetOs.Windows11;
        if (up.Contains("WIN10") || up.Contains("WINDOWS10") || up.Contains("WINDOWS_10")) return TargetOs.Windows10;
        return null;
    }

    /// <summary>Extrai a versão do rótulo (ex.: "Ubuntu 24.04.1 LTS amd64" -> "24.04.1").</summary>
    static string VersionIn(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var m = Regex.Match(text, @"(\d+\.\d+(\.\d+)?)");
        if (m.Success) return m.Groups[1].Value;
        // Rótulos com a versão separada por hífen (Rocky-9-4-x86_64, Fedora-WS-Live-40-1-14).
        m = Regex.Match(text, @"[-_](\d{1,2})[-_](\d{1,2})[-_]");
        return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}" : "";
    }

    // ------------------------------------------------------------------
    // Leitura ISO 9660
    // ------------------------------------------------------------------
    static byte[] ReadSector(FileStream fs, long offset)
    {
        var buf = new byte[SectorSize];
        fs.Seek(offset, SeekOrigin.Begin);
        int read = 0;
        while (read < SectorSize)
        {
            int n = fs.Read(buf, read, SectorSize - read);
            if (n <= 0) break;
            read += n;
        }
        return buf;
    }

    static string Ascii(byte[] buf, int offset, int len)
        => Encoding.ASCII.GetString(buf, offset, len).TrimEnd('\0', ' ').Trim();

    /// <summary>Nomes das entradas do diretório raiz (arquivos e pastas do primeiro nível).</summary>
    static List<string> ReadRootEntries(FileStream fs, byte[] pvd)
    {
        var names = new List<string>();
        try
        {
            // Registro do diretório raiz embutido no PVD (34 bytes a partir do offset 156).
            uint extent = BitConverter.ToUInt32(pvd, 156 + 2);
            uint size = BitConverter.ToUInt32(pvd, 156 + 10);
            if (extent == 0 || size == 0 || size > 4 * 1024 * 1024) return names;

            var data = new byte[size];
            fs.Seek((long)extent * SectorSize, SeekOrigin.Begin);
            int read = 0;
            while (read < data.Length)
            {
                int n = fs.Read(data, read, data.Length - read);
                if (n <= 0) break;
                read += n;
            }

            int pos = 0;
            while (pos < read)
            {
                int recLen = data[pos];
                if (recLen == 0)
                {
                    // Fim dos registros neste setor: pula para o próximo.
                    pos = (pos / SectorSize + 1) * SectorSize;
                    if (pos >= read) break;
                    continue;
                }
                if (pos + recLen > read) break;

                int nameLen = data[pos + 32];
                if (nameLen > 0 && pos + 33 + nameLen <= read)
                {
                    var raw = Encoding.ASCII.GetString(data, pos + 33, nameLen);
                    // 0x00 = "." e 0x01 = ".." (entradas especiais do ISO 9660)
                    if (nameLen != 1 || (data[pos + 33] != 0 && data[pos + 33] != 1))
                        names.Add(raw.Split(';')[0]);
                }
                pos += recLen;
            }
        }
        catch { /* melhor esforço: a identificação por rótulo já cobre a maioria dos casos */ }
        return names;
    }
}
