using System.Text;
using IsoForge.Models;

namespace IsoForge.Core.Linux;

/// <summary>
/// Gera o <c>preseed.cfg</c> do debian-installer (Debian) e do Ubiquity (Linux Mint):
/// responde a todas as perguntas da instalação e chama o payload do IsoForge no
/// <c>late_command</c>. É o equivalente Debian do autounattend.xml.
/// </summary>
public static class PreseedGenerator
{
    public const string FileName = "preseed.cfg";

    public static string Generate(BuildConfig c)
    {
        var lx = c.Linux;
        var sb = new StringBuilder();
        var hostname = LinuxPostInstall.Slug(string.IsNullOrWhiteSpace(c.ComputerName) ? "isoforge-pc" : c.ComputerName);
        var passwordHash = UnixCrypt.Sha512(c.Password);
        bool mint = c.Os == TargetOs.LinuxMint;

        sb.AppendLine("# ================================================================");
        sb.AppendLine("# IsoForge - instalacao desassistida (preseed)");
        sb.AppendLine($"# Sistema: {OsCatalog.NameOf(c.Os)}");
        sb.AppendLine("# Senha do usuario cifrada em SHA-512 crypt (nunca em texto puro).");
        sb.AppendLine("# ================================================================");
        sb.AppendLine();

        sb.AppendLine("### Idioma, teclado e fuso");
        sb.AppendLine($"d-i debian-installer/locale string {lx.LocaleId}");
        sb.AppendLine("d-i debian-installer/language string " + LanguageOf(lx.LocaleId));
        sb.AppendLine("d-i debian-installer/country string " + CountryOf(lx.LocaleId));
        sb.AppendLine("d-i localechooser/supported-locales multiselect " + lx.LocaleId);
        sb.AppendLine("d-i keyboard-configuration/xkb-keymap select " + lx.KeyboardLayout);
        if (!string.IsNullOrWhiteSpace(lx.KeyboardVariant))
            sb.AppendLine($"d-i keyboard-configuration/variant select {lx.KeyboardLayout} ({lx.KeyboardVariant})");
        sb.AppendLine("d-i console-setup/ask_detect boolean false");
        sb.AppendLine($"d-i time/zone string {lx.Timezone}");
        sb.AppendLine("d-i clock-setup/utc boolean true");
        sb.AppendLine("d-i clock-setup/ntp boolean true");
        sb.AppendLine();

        sb.AppendLine("### Rede");
        sb.AppendLine("d-i netcfg/choose_interface select auto");
        sb.AppendLine($"d-i netcfg/get_hostname string {hostname}");
        sb.AppendLine("d-i netcfg/get_domain string local");
        sb.AppendLine("d-i netcfg/hostname string " + hostname);
        sb.AppendLine("d-i hw-detect/load_firmware boolean " + (lx.ProprietaryDrivers ? "true" : "false"));
        if (c.AutoConnectWifi && !string.IsNullOrWhiteSpace(c.WifiSsid))
        {
            sb.AppendLine($"d-i netcfg/wireless_essid string {c.WifiSsid}");
            sb.AppendLine("d-i netcfg/wireless_security_type select wpa");
            sb.AppendLine($"d-i netcfg/wireless_wpa string {c.WifiPassword}");
        }
        sb.AppendLine();

        sb.AppendLine("### Usuarios");
        sb.AppendLine($"d-i passwd/root-login boolean {(lx.DisableRootPassword ? "false" : "true")}");
        sb.AppendLine("d-i passwd/make-user boolean true");
        sb.AppendLine($"d-i passwd/user-fullname string {(string.IsNullOrWhiteSpace(lx.FullName) ? c.UserName : lx.FullName)}");
        sb.AppendLine($"d-i passwd/username string {c.UserName}");
        sb.AppendLine($"d-i passwd/user-password-crypted password {passwordHash}");
        if (c.IsAdministrator)
            sb.AppendLine("d-i passwd/user-default-groups string audio cdrom video sudo plugdev netdev");
        else
            sb.AppendLine("d-i passwd/user-default-groups string audio cdrom video plugdev netdev");
        sb.AppendLine("d-i user-setup/allow-password-weak boolean true");
        sb.AppendLine($"d-i user-setup/encrypt-home boolean false");
        sb.AppendLine();

        sb.AppendLine("### Particionamento");
        if (!string.IsNullOrWhiteSpace(lx.TargetDisk))
            sb.AppendLine($"d-i partman-auto/disk string {lx.TargetDisk}");
        else
            sb.AppendLine("# Sem disco fixado: o instalador usa o unico disco disponivel.");
        sb.AppendLine("d-i partman-auto/method string " + lx.DiskMode switch
        {
            LinuxDiskMode.EntireDisk => "regular",
            LinuxDiskMode.EntireDiskLvm => "lvm",
            _ => "crypto"
        });
        if (lx.DiskMode == LinuxDiskMode.EntireDiskEncrypted && !string.IsNullOrWhiteSpace(lx.DiskPassword))
        {
            sb.AppendLine($"d-i partman-crypto/passphrase password {lx.DiskPassword}");
            sb.AppendLine($"d-i partman-crypto/passphrase-again password {lx.DiskPassword}");
        }
        sb.AppendLine("d-i partman-auto/choose_recipe select atomic");
        sb.AppendLine("d-i partman-lvm/device_remove_lvm boolean true");
        sb.AppendLine("d-i partman-md/device_remove_md boolean true");
        sb.AppendLine("d-i partman-lvm/confirm boolean true");
        sb.AppendLine("d-i partman-lvm/confirm_nooverwrite boolean true");
        sb.AppendLine("d-i partman-partitioning/confirm_write_new_label boolean true");
        sb.AppendLine("d-i partman/choose_partition select finish");
        sb.AppendLine("d-i partman/confirm boolean true");
        sb.AppendLine("d-i partman/confirm_nooverwrite boolean true");
        sb.AppendLine("d-i partman-efi/non_efi_system boolean true");
        sb.AppendLine();

        sb.AppendLine("### Pacotes");
        sb.AppendLine("d-i apt-setup/non-free boolean true");
        sb.AppendLine("d-i apt-setup/non-free-firmware boolean true");
        sb.AppendLine("d-i apt-setup/contrib boolean true");
        sb.AppendLine("tasksel tasksel/first multiselect " + TaskselFor(c));
        sb.AppendLine("d-i pkgsel/include string " + string.Join(" ", AutoinstallGenerator.BasePackages(c)));
        sb.AppendLine($"d-i pkgsel/upgrade select {(lx.UpdateDuringInstall ? "full-upgrade" : "none")}");
        sb.AppendLine("popularity-contest popularity-contest/participate boolean false");
        sb.AppendLine("d-i pkgsel/update-policy select unattended-upgrades");
        sb.AppendLine();

        sb.AppendLine("### Bootloader");
        sb.AppendLine("d-i grub-installer/only_debian boolean true");
        sb.AppendLine("d-i grub-installer/with_other_os boolean true");
        sb.AppendLine("d-i grub-installer/bootdev string default");
        sb.AppendLine();

        if (mint)
        {
            sb.AppendLine("### Ubiquity (Linux Mint)");
            sb.AppendLine("ubiquity ubiquity/reboot boolean true");
            sb.AppendLine("ubiquity ubiquity/summary note");
            sb.AppendLine("ubiquity ubiquity/success_command string " + LateCommand(c, forUbiquity: true));
            sb.AppendLine();
        }

        sb.AppendLine("### Finalizacao");
        sb.AppendLine("d-i finish-install/reboot_in_progress note");
        sb.AppendLine("d-i debian-installer/exit/poweroff boolean false");
        sb.AppendLine();
        sb.AppendLine("### IsoForge: copia o payload e habilita o servico de primeiro boot");
        sb.AppendLine("d-i preseed/late_command string " + LateCommand(c, forUbiquity: false));

        return sb.ToString();
    }

    /// <summary>Conjunto de tarefas (ambiente gráfico) instalado pelo tasksel.</summary>
    static string TaskselFor(BuildConfig c) => c.Linux.Desktop switch
    {
        LinuxDesktop.None => "standard, ssh-server",
        LinuxDesktop.Kde => "standard, desktop, kde-desktop",
        LinuxDesktop.Xfce => "standard, desktop, xfce-desktop",
        LinuxDesktop.Gnome => "standard, desktop, gnome-desktop",
        _ => "standard, desktop"
    };

    /// <summary>
    /// Comandos finais: rodam no ambiente do instalador com o sistema montado em /target
    /// (Ubiquity monta em /target também). Tudo em uma linha, separado por ';' — exigência do preseed.
    /// </summary>
    static string LateCommand(BuildConfig c, bool forUbiquity)
    {
        var target = forUbiquity ? "/target" : "/target";
        var payload = LinuxPostInstall.PayloadDirOnDisk;
        var folder = LinuxAnswerFile.PayloadFolderOnIso;
        var svc = LinuxAnswerFile.FirstBootServiceName;
        var inTarget = forUbiquity ? $"chroot {target}" : "in-target";

        var parts = new List<string>
        {
            $"mkdir -p {target}{payload}",
            $"for d in /cdrom/{folder} /media/{folder} /media/cdrom/{folder} /run/media/*/{folder}; do [ -d \"$d\" ] && cp -a \"$d/.\" {target}{payload}/ && break; done",
            $"chmod +x {target}{payload}/*.sh || true",
            $"cp {target}{payload}/{svc} {target}/etc/systemd/system/{svc}",
            $"{inTarget} systemctl enable {svc}"
        };
        if (c.Linux.AutoLogin)
        {
            parts.Add($"mkdir -p {target}/etc/lightdm {target}/etc/gdm3");
            parts.Add($"printf '[Seat:*]\\nautologin-user={c.UserName}\\n' > {target}/etc/lightdm/lightdm.conf");
            parts.Add($"printf '[daemon]\\nAutomaticLoginEnable=true\\nAutomaticLogin={c.UserName}\\n' > {target}/etc/gdm3/custom.conf");
        }
        return string.Join("; ", parts);
    }

    static string LanguageOf(string locale) => locale.Split('_', '.')[0];

    static string CountryOf(string locale)
    {
        var parts = locale.Split('_');
        return parts.Length > 1 ? parts[1].Split('.')[0] : "BR";
    }
}
