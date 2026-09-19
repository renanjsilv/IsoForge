using System.Text;
using IsoForge.Models;

namespace IsoForge.Core.Linux;

/// <summary>
/// Gera o <c>ks.cfg</c> (Kickstart do Anaconda) usado por Fedora, Rocky Linux e AlmaLinux.
/// É o equivalente RHEL do autounattend.xml: responde a instalação inteira e executa o
/// payload do IsoForge na seção %post.
/// </summary>
public static class KickstartGenerator
{
    public const string FileName = "ks.cfg";

    public static string Generate(BuildConfig c)
    {
        var lx = c.Linux;
        var sb = new StringBuilder();
        var hostname = LinuxPostInstall.Slug(string.IsNullOrWhiteSpace(c.ComputerName) ? "isoforge-pc" : c.ComputerName);
        var passwordHash = UnixCrypt.Sha512(c.Password);

        sb.AppendLine("# ================================================================");
        sb.AppendLine("# IsoForge - instalacao desassistida (Kickstart / Anaconda)");
        sb.AppendLine($"# Sistema: {OsCatalog.NameOf(c.Os)}");
        sb.AppendLine("# Senha do usuario cifrada em SHA-512 crypt (nunca em texto puro).");
        sb.AppendLine("# ================================================================");
        sb.AppendLine("text");
        sb.AppendLine("eula --agreed");
        sb.AppendLine("firstboot --disable");
        sb.AppendLine("reboot");
        sb.AppendLine();

        sb.AppendLine($"lang {lx.LocaleId}");
        sb.AppendLine($"keyboard --xlayouts='{XLayout(lx)}'");
        sb.AppendLine($"timezone {lx.Timezone} --utc");
        sb.AppendLine();

        sb.AppendLine("# Rede");
        sb.AppendLine($"network --bootproto=dhcp --device=link --activate --hostname={hostname}");
        if (c.AutoConnectWifi && !string.IsNullOrWhiteSpace(c.WifiSsid))
            sb.AppendLine($"# Wi-Fi ({c.WifiSsid}) configurado no primeiro boot pelo NetworkManager.");
        sb.AppendLine("firewall --enabled --service=ssh");
        sb.AppendLine("selinux --enforcing");
        sb.AppendLine();

        sb.AppendLine("# Usuarios");
        sb.AppendLine(lx.DisableRootPassword ? "rootpw --lock" : $"rootpw --iscrypted {passwordHash}");
        var groups = c.IsAdministrator ? " --groups=wheel" : "";
        var gecos = string.IsNullOrWhiteSpace(lx.FullName) ? c.UserName : lx.FullName;
        sb.AppendLine($"user --name={c.UserName} --gecos=\"{gecos}\"{groups} --iscrypted --password={passwordHash}");
        if (!string.IsNullOrWhiteSpace(lx.SshAuthorizedKey))
            sb.AppendLine($"sshkey --username={c.UserName} \"{lx.SshAuthorizedKey.Trim()}\"");
        sb.AppendLine();

        sb.AppendLine("# Disco");
        if (!string.IsNullOrWhiteSpace(lx.TargetDisk))
        {
            var dev = lx.TargetDisk.Replace("/dev/", "");
            sb.AppendLine($"ignoredisk --only-use={dev}");
            sb.AppendLine($"clearpart --all --initlabel --drives={dev}");
        }
        else
        {
            // Sem disco fixado: usa o primeiro disco fixo e ignora a midia de instalacao.
            sb.AppendLine("clearpart --all --initlabel");
        }
        var autopart = lx.DiskMode switch
        {
            LinuxDiskMode.EntireDisk => "autopart --type=plain",
            LinuxDiskMode.EntireDiskLvm => "autopart --type=lvm",
            _ => $"autopart --type=lvm --encrypted --passphrase={lx.DiskPassword}"
        };
        sb.AppendLine(autopart);
        sb.AppendLine("bootloader --location=mbr --boot-drive=" + (string.IsNullOrWhiteSpace(lx.TargetDisk) ? "" : lx.TargetDisk.Replace("/dev/", "")));
        sb.AppendLine();

        sb.AppendLine("%packages");
        sb.AppendLine(EnvironmentGroup(c));
        foreach (var p in AutoinstallGenerator.BasePackages(c)) sb.AppendLine(p);
        if (lx.MinimalInstall)
        {
            sb.AppendLine("-@guest-desktop-agents");
            sb.AppendLine("-@libreoffice");
        }
        sb.AppendLine("%end");
        sb.AppendLine();

        // O payload esta na midia de instalacao (montada em /run/install/repo). Esta secao roda
        // FORA do chroot para conseguir ler a midia e gravar no sistema instalado (/mnt/sysroot).
        sb.AppendLine("%post --nochroot --log=/mnt/sysroot/var/log/isoforge-kickstart.log");
        sb.AppendLine("set -x");
        sb.AppendLine("SYSROOT=/mnt/sysroot");
        sb.AppendLine("[ -d \"$SYSROOT\" ] || SYSROOT=/mnt/sysimage");
        sb.AppendLine($"mkdir -p \"$SYSROOT{LinuxPostInstall.PayloadDirOnDisk}\"");
        sb.AppendLine($"for d in /run/install/repo/{LinuxAnswerFile.PayloadFolderOnIso} " +
                      $"/run/install/sources/mount-*/{LinuxAnswerFile.PayloadFolderOnIso} " +
                      $"/mnt/install/repo/{LinuxAnswerFile.PayloadFolderOnIso}; do");
        sb.AppendLine($"  [ -d \"$d\" ] && cp -a \"$d/.\" \"$SYSROOT{LinuxPostInstall.PayloadDirOnDisk}/\" && break");
        sb.AppendLine("done");
        sb.AppendLine($"chmod +x \"$SYSROOT{LinuxPostInstall.PayloadDirOnDisk}\"/*.sh 2>/dev/null || true");
        sb.AppendLine($"install -m 0644 \"$SYSROOT{LinuxPostInstall.PayloadDirOnDisk}/{LinuxAnswerFile.FirstBootServiceName}\" \\");
        sb.AppendLine($"  \"$SYSROOT/etc/systemd/system/{LinuxAnswerFile.FirstBootServiceName}\" 2>/dev/null || true");
        sb.AppendLine("%end");
        sb.AppendLine();

        // Dentro do chroot: habilita o servico e ajusta o login automatico.
        sb.AppendLine("%post --log=/var/log/isoforge-kickstart-chroot.log");
        sb.AppendLine("set -x");
        sb.AppendLine($"systemctl enable {LinuxAnswerFile.FirstBootServiceName} || true");
        if (lx.AutoLogin)
        {
            sb.AppendLine("mkdir -p /etc/gdm");
            sb.AppendLine($"printf '[daemon]\\nAutomaticLoginEnable=True\\nAutomaticLogin={c.UserName}\\n' > /etc/gdm/custom.conf");
        }
        if (lx.ProprietaryDrivers)
        {
            sb.AppendLine("# RPM Fusion (drivers e codecs proprietarios) — habilitado no primeiro boot.");
            sb.AppendLine("touch /var/lib/isoforge-enable-rpmfusion 2>/dev/null || true");
        }
        sb.AppendLine("%end");

        return sb.ToString();
    }

    /// <summary>Grupo/ambiente instalado conforme o ambiente gráfico escolhido.</summary>
    static string EnvironmentGroup(BuildConfig c) => (c.Os, c.Linux.Desktop) switch
    {
        (_, LinuxDesktop.None) => "@^minimal-environment",
        (TargetOs.Fedora, LinuxDesktop.Kde) => "@^kde-desktop-environment",
        (TargetOs.Fedora, LinuxDesktop.Xfce) => "@^xfce-desktop-environment",
        (TargetOs.Fedora, _) => "@^workstation-product-environment",
        (_, LinuxDesktop.Kde) => "@^kde-desktop-environment",
        (_, LinuxDesktop.Xfce) => "@^xfce-desktop-environment",
        _ => "@^graphical-server-environment"
    };

    /// <summary>Layout do teclado no formato aceito pelo Anaconda (ex.: "br (abnt2)").</summary>
    static string XLayout(LinuxConfig lx) =>
        string.IsNullOrWhiteSpace(lx.KeyboardVariant) ? lx.KeyboardLayout : $"{lx.KeyboardLayout} ({lx.KeyboardVariant})";
}
