namespace IsoForge.Models;

/// <summary>Sistema operacional de destino da ISO gerada.</summary>
public enum TargetOs
{
    Windows11,
    Windows10,
    Ubuntu,
    UbuntuServer,
    Debian,
    LinuxMint,
    Fedora,
    RockyLinux,
    AlmaLinux,
    OpenSuse,
    ArchLinux
}

/// <summary>Família do sistema (define gerenciador de pacotes e mecanismos de automação).</summary>
public enum OsFamily
{
    Windows,
    /// <summary>Debian/Ubuntu e derivados — apt/dpkg.</summary>
    Debian,
    /// <summary>Fedora/RHEL e derivados — dnf/rpm.</summary>
    RedHat,
    /// <summary>openSUSE/SLE — zypper/rpm.</summary>
    Suse,
    /// <summary>Arch e derivados — pacman.</summary>
    Arch
}

/// <summary>Mecanismo de instalação desassistida suportado pelo instalador da distro.</summary>
public enum UnattendKind
{
    /// <summary>autounattend.xml (Windows Setup).</summary>
    WindowsUnattend,
    /// <summary>autoinstall / cloud-init NoCloud (subiquity — Ubuntu 20.04+).</summary>
    CloudInitAutoinstall,
    /// <summary>preseed.cfg (debian-installer / Ubiquity).</summary>
    DebianPreseed,
    /// <summary>ks.cfg (Anaconda — Fedora/RHEL e derivados).</summary>
    Kickstart,
    /// <summary>autoinst.xml (AutoYaST — openSUSE/SLE).</summary>
    AutoYast,
    /// <summary>user_configuration.json (archinstall).</summary>
    ArchInstall
}

/// <summary>Metadados de um sistema operacional suportado.</summary>
/// <param name="Id">Identificador interno.</param>
/// <param name="Name">Nome exibido ao usuário.</param>
/// <param name="ShortName">Slug em minúsculas (nomes de arquivo, logs).</param>
/// <param name="Family">Família (gerenciador de pacotes).</param>
/// <param name="Unattend">Mecanismo de instalação desassistida.</param>
/// <param name="PackageManager">Comando do gerenciador de pacotes (apt-get, dnf...).</param>
/// <param name="Glyph">Ícone (Segoe MDL2 Assets) usado nos cards.</param>
/// <param name="Accent">Cor de destaque do card (hex).</param>
/// <param name="Tagline">Descrição curta mostrada na seleção.</param>
/// <param name="AnswerFileName">Nome do arquivo de resposta gerado.</param>
public record OsInfo(
    TargetOs Id,
    string Name,
    string ShortName,
    OsFamily Family,
    UnattendKind Unattend,
    string PackageManager,
    string Glyph,
    string Accent,
    string Tagline,
    string AnswerFileName)
{
    public bool IsWindows => Family == OsFamily.Windows;
    public bool IsLinux => Family != OsFamily.Windows;
}

/// <summary>Catálogo dos sistemas operacionais que o IsoForge sabe personalizar.</summary>
public static class OsCatalog
{
    public static readonly IReadOnlyList<OsInfo> All = new List<OsInfo>
    {
        new(TargetOs.Windows11, "Windows 11", "windows11", OsFamily.Windows, UnattendKind.WindowsUnattend,
            "", "", "#2563EB",
            "ISO oficial da Microsoft. Conta local ou Entra ID, apps silenciosos, drivers por modelo.",
            "autounattend.xml"),

        new(TargetOs.Windows10, "Windows 10", "windows10", OsFamily.Windows, UnattendKind.WindowsUnattend,
            "", "", "#0EA5E9",
            "Mesmo fluxo do Windows 11, sem os requisitos de TPM/Secure Boot.",
            "autounattend.xml"),

        new(TargetOs.Ubuntu, "Ubuntu Desktop", "ubuntu", OsFamily.Debian, UnattendKind.CloudInitAutoinstall,
            "apt-get", "", "#E95420",
            "22.04 LTS ou mais novo. Instalação desassistida via autoinstall (cloud-init).",
            "user-data"),

        new(TargetOs.UbuntuServer, "Ubuntu Server", "ubuntu-server", OsFamily.Debian, UnattendKind.CloudInitAutoinstall,
            "apt-get", "", "#772953",
            "Servidor sem interface gráfica. Mesmo autoinstall do Ubuntu Desktop.",
            "user-data"),

        new(TargetOs.Debian, "Debian", "debian", OsFamily.Debian, UnattendKind.DebianPreseed,
            "apt-get", "", "#A80030",
            "Debian 12/13. Instalação desassistida via preseed do debian-installer.",
            "preseed.cfg"),

        new(TargetOs.LinuxMint, "Linux Mint", "linuxmint", OsFamily.Debian, UnattendKind.DebianPreseed,
            "apt-get", "", "#87CF3E",
            "Base Ubuntu com Cinnamon. Desassistido via preseed do Ubiquity.",
            "preseed.cfg"),

        new(TargetOs.Fedora, "Fedora", "fedora", OsFamily.RedHat, UnattendKind.Kickstart,
            "dnf", "", "#51A2DA",
            "Fedora Workstation/Server. Instalação desassistida via Kickstart (Anaconda).",
            "ks.cfg"),

        new(TargetOs.RockyLinux, "Rocky Linux", "rocky", OsFamily.RedHat, UnattendKind.Kickstart,
            "dnf", "", "#10B981",
            "Compatível com RHEL. Instalação desassistida via Kickstart.",
            "ks.cfg"),

        new(TargetOs.AlmaLinux, "AlmaLinux", "almalinux", OsFamily.RedHat, UnattendKind.Kickstart,
            "dnf", "", "#0F4266",
            "Compatível com RHEL. Instalação desassistida via Kickstart.",
            "ks.cfg"),

        new(TargetOs.OpenSuse, "openSUSE", "opensuse", OsFamily.Suse, UnattendKind.AutoYast,
            "zypper", "", "#73BA25",
            "Leap ou Tumbleweed. Instalação desassistida via AutoYaST.",
            "autoinst.xml"),

        new(TargetOs.ArchLinux, "Arch Linux", "arch", OsFamily.Arch, UnattendKind.ArchInstall,
            "pacman", "", "#1793D1",
            "ISO oficial. Instalação desassistida via archinstall (perfil JSON).",
            "user_configuration.json"),
    };

    public static OsInfo Get(TargetOs os) => All.First(o => o.Id == os);

    public static bool IsLinux(TargetOs os) => Get(os).IsLinux;
    public static bool IsWindows(TargetOs os) => Get(os).IsWindows;

    public static OsFamily FamilyOf(TargetOs os) => Get(os).Family;

    /// <summary>Nome exibido (ex.: "Ubuntu Desktop").</summary>
    public static string NameOf(TargetOs os) => Get(os).Name;

    /// <summary>Sistemas da mesma família, na ordem do catálogo.</summary>
    public static IEnumerable<OsInfo> InFamily(OsFamily family) => All.Where(o => o.Family == family);
}
