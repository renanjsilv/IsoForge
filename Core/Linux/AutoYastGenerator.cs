using System.IO;
using System.Text;
using System.Xml.Linq;
using IsoForge.Models;

namespace IsoForge.Core.Linux;

/// <summary>
/// Gera o <c>autoinst.xml</c> (AutoYaST) usado pelo openSUSE Leap/Tumbleweed.
/// É o equivalente SUSE do autounattend.xml: perfil XML que responde a instalação inteira
/// e executa o payload do IsoForge nos scripts de chroot.
/// </summary>
public static class AutoYastGenerator
{
    public const string FileName = "autoinst.xml";

    static readonly XNamespace Ns = "http://www.suse.com/1.0/yast2ns";
    static readonly XNamespace Cfg = "http://www.suse.com/1.0/configns";

    /// <summary>Atributo config:type usado pelo AutoYaST para tipar valores.</summary>
    static XAttribute Type(string type) => new(Cfg + "type", type);

    static XElement Bool(string name, bool value) => new(Ns + name, Type("boolean"), value ? "true" : "false");

    public static XDocument GenerateDocument(BuildConfig c)
    {
        var lx = c.Linux;
        var hostname = LinuxPostInstall.Slug(string.IsNullOrWhiteSpace(c.ComputerName) ? "isoforge-pc" : c.ComputerName);
        var passwordHash = UnixCrypt.Sha512(c.Password);

        var profile = new XElement(Ns + "profile",
            new XAttribute(XNamespace.Xmlns + "config", Cfg.NamespaceName),

            new XElement(Ns + "language",
                new XElement(Ns + "language", lx.LocaleId.Split('.')[0]),
                new XElement(Ns + "languages", "")),

            new XElement(Ns + "keyboard",
                new XElement(Ns + "keymap", Keymap(lx.KeyboardLayout))),

            new XElement(Ns + "timezone",
                new XElement(Ns + "hwclock", "UTC"),
                new XElement(Ns + "timezone", lx.Timezone)),

            new XElement(Ns + "networking",
                Bool("keep_install_network", true),
                new XElement(Ns + "dns", new XElement(Ns + "hostname", hostname))),

            new XElement(Ns + "bootloader",
                new XElement(Ns + "loader_type", "grub2-efi")),

            Partitioning(lx),
            Users(c, passwordHash),
            Software(c),
            Services(),
            Scripts(c));

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("profile", null, null, null),
            profile);
    }

    public static string Generate(BuildConfig c)
    {
        var doc = GenerateDocument(c);
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        doc.Save(writer, SaveOptions.None);
        return sb.ToString() + Environment.NewLine;
    }

    // ------------------------------------------------------------------
    static XElement Partitioning(LinuxConfig lx)
    {
        var drive = new XElement(Ns + "drive",
            new XElement(Ns + "device", string.IsNullOrWhiteSpace(lx.TargetDisk) ? "/dev/sda" : lx.TargetDisk),
            new XElement(Ns + "use", "all"),
            Bool("initialize", true),
            new XElement(Ns + "type", Type("symbol"),
                lx.DiskMode == LinuxDiskMode.EntireDisk ? "CT_DISK" : "CT_LVM"));

        if (lx.DiskMode == LinuxDiskMode.EntireDiskEncrypted && !string.IsNullOrWhiteSpace(lx.DiskPassword))
            drive.Add(new XElement(Ns + "crypt_key", lx.DiskPassword), Bool("crypt_fs", true));

        return new XElement(Ns + "partitioning", Type("list"), drive);
    }

    static XElement Users(BuildConfig c, string passwordHash)
    {
        var lx = c.Linux;
        var users = new XElement(Ns + "users", Type("list"));

        users.Add(new XElement(Ns + "user",
            new XElement(Ns + "username", "root"),
            new XElement(Ns + "user_password", lx.DisableRootPassword ? "!" : passwordHash),
            Bool("encrypted", true)));

        var user = new XElement(Ns + "user",
            new XElement(Ns + "username", c.UserName),
            new XElement(Ns + "fullname", string.IsNullOrWhiteSpace(lx.FullName) ? c.UserName : lx.FullName),
            new XElement(Ns + "user_password", passwordHash),
            Bool("encrypted", true));
        if (c.IsAdministrator)
            user.Add(new XElement(Ns + "gid", "100"));
        users.Add(user);
        return users;
    }

    static XElement Software(BuildConfig c)
    {
        var patterns = new XElement(Ns + "patterns", Type("list"),
            new XElement(Ns + "pattern", "base"),
            new XElement(Ns + "pattern", "enhanced_base"));

        switch (c.Linux.Desktop)
        {
            case LinuxDesktop.None: break;
            case LinuxDesktop.Kde:
                patterns.Add(new XElement(Ns + "pattern", "kde"), new XElement(Ns + "pattern", "x11"));
                break;
            case LinuxDesktop.Xfce:
                patterns.Add(new XElement(Ns + "pattern", "xfce"), new XElement(Ns + "pattern", "x11"));
                break;
            default:
                patterns.Add(new XElement(Ns + "pattern", "gnome"), new XElement(Ns + "pattern", "x11"));
                break;
        }

        var packages = new XElement(Ns + "packages", Type("list"));
        foreach (var p in AutoinstallGenerator.BasePackages(c))
            packages.Add(new XElement(Ns + "package", p));

        return new XElement(Ns + "software",
            patterns,
            packages,
            Bool("install_recommended", true));
    }

    static XElement Services() =>
        new(Ns + "services-manager",
            new XElement(Ns + "services", Type("list"),
                new XElement(Ns + "service",
                    new XElement(Ns + "service_name", LinuxAnswerFile.FirstBootServiceName.Replace(".service", "")),
                    new XElement(Ns + "service_status", "enable"))));

    /// <summary>
    /// Script de chroot: copia o payload do IsoForge da mídia de instalação para o sistema
    /// recém-instalado e habilita o serviço de primeiro boot.
    /// </summary>
    static XElement Scripts(BuildConfig c)
    {
        var payload = LinuxPostInstall.PayloadDirOnDisk;
        var folder = LinuxAnswerFile.PayloadFolderOnIso;
        var svc = LinuxAnswerFile.FirstBootServiceName;

        var sh = new StringBuilder();
        sh.AppendLine("#!/bin/sh");
        sh.AppendLine("# IsoForge: copia o payload da midia para o sistema instalado (/mnt).");
        sh.AppendLine("set -x");
        sh.AppendLine($"mkdir -p /mnt{payload}");
        sh.AppendLine($"for d in /run/initramfs/live/{folder} /var/adm/mount/{folder} /mounts/mp_0000/{folder} /cdrom/{folder}; do");
        sh.AppendLine($"  [ -d \"$d\" ] && cp -a \"$d/.\" /mnt{payload}/ && break");
        sh.AppendLine("done");
        sh.AppendLine($"chmod +x /mnt{payload}/*.sh 2>/dev/null || true");
        sh.AppendLine($"install -m 0644 /mnt{payload}/{svc} /mnt/etc/systemd/system/{svc} 2>/dev/null || true");
        sh.AppendLine($"chroot /mnt systemctl enable {svc} 2>/dev/null || true");
        if (c.Linux.AutoLogin)
        {
            sh.AppendLine("mkdir -p /mnt/etc/sysconfig/displaymanager");
            sh.AppendLine($"printf 'DISPLAYMANAGER_AUTOLOGIN=\"{c.UserName}\"\\n' >> /mnt/etc/sysconfig/displaymanager");
        }

        return new XElement(Ns + "scripts",
            new XElement(Ns + "chroot-scripts", Type("list"),
                new XElement(Ns + "script",
                    new XElement(Ns + "filename", "isoforge-payload.sh"),
                    Bool("chrooted", false),
                    new XElement(Ns + "interpreter", "shell"),
                    new XElement(Ns + "source", new XCData(sh.ToString())))));
    }

    /// <summary>Nome do mapa de teclado no formato do YaST (ex.: br -> portuguese-br).</summary>
    static string Keymap(string layout) => layout switch
    {
        "br" => "portuguese-br-abnt2",
        "pt" => "portuguese",
        "us" => "english-us",
        "es" => "spanish",
        _ => layout
    };
}
