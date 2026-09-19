using System.IO;
using System.Text;
using IsoForge.Models;

namespace IsoForge.Core.Linux;

/// <summary>
/// Gera o <c>isoforge-postinstall.sh</c>: o equivalente Linux do <c>install.cmd</c> do Windows.
/// Roda uma única vez no primeiro boot (unidade systemd) como root e cuida de:
/// repositórios oficiais + aplicativos, Wi-Fi, aparência (papel de parede, tema escuro, dock),
/// VPN, otimização (debloat), seleção de unidade (hostname pelo nº de série) e relatório HTML.
///
/// O script é ASCII puro (sem acentuação) para não depender do locale do instalador, e é
/// idempotente: o marcador em /var/lib/isoforge impede a reexecução em reboots seguintes.
/// </summary>
public static class LinuxPostInstall
{
    public const string FileName = "isoforge-postinstall.sh";
    public const string PayloadDirOnDisk = "/opt/isoforge";
    public const string LogPath = "/var/log/isoforge-postinstall.log";
    public const string DoneMarker = "/var/lib/isoforge/postinstall.done";
    public const string SelectUnitFileName = "isoforge-select-unit";
    public const string SetHostnameFileName = "isoforge-set-hostname";

    /// <summary>Escapa um valor para uso entre aspas simples no shell.</summary>
    public static string Sh(string? value) => "'" + (value ?? "").Replace("'", "'\\''") + "'";

    public static string Generate(BuildConfig c)
    {
        var os = OsCatalog.Get(c.Os);
        var sb = new StringBuilder();

        Header(sb, c, os);
        PackageManagerHelpers(sb, os.Family);
        Wifi(sb, c);
        WaitForNetwork(sb, c);
        Flatpak(sb, c);
        Apps(sb, c);
        ExtraPackages(sb, c);
        Ssh(sb, c);
        Appearance(sb, c);
        Vpn(sb, c);
        Debloat(sb, c, os.Family);
        UnitSelection(sb, c);
        PostScript(sb, c);
        Report(sb, c);
        Footer(sb, c);

        return sb.ToString();
    }

    // ------------------------------------------------------------------
    static void Header(StringBuilder sb, BuildConfig c, OsInfo os)
    {
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("# ================================================================");
        sb.AppendLine("# IsoForge - pos-instalacao (gerado automaticamente)");
        sb.AppendLine($"# Sistema: {Ascii(os.Name)}   Gerenciador de pacotes: {os.PackageManager}");
        sb.AppendLine("# Roda uma unica vez no primeiro boot, como root.");
        sb.AppendLine("# ================================================================");
        sb.AppendLine("set -u");
        sb.AppendLine();
        sb.AppendLine($"LOGFILE={Sh(LogPath)}");
        sb.AppendLine($"DONE_MARKER={Sh(DoneMarker)}");
        sb.AppendLine($"PAYLOAD={Sh(PayloadDirOnDisk)}");
        sb.AppendLine("mkdir -p \"$(dirname \"$LOGFILE\")\" \"$(dirname \"$DONE_MARKER\")\"");
        sb.AppendLine("exec > >(tee -a \"$LOGFILE\") 2>&1");
        sb.AppendLine();
        sb.AppendLine("log() { echo \"[IsoForge $(date '+%H:%M:%S')] $*\"; }");
        sb.AppendLine("run() { log \"\\$ $*\"; \"$@\"; local rc=$?; [ $rc -ne 0 ] && log \"AVISO: comando terminou com codigo $rc\"; return 0; }");
        sb.AppendLine();
        sb.AppendLine("# Idempotencia: se ja rodou, sai (evita repetir em reboots).");
        sb.AppendLine("if [ -f \"$DONE_MARKER\" ]; then");
        sb.AppendLine("  log \"pos-instalacao ja concluida anteriormente; saindo.\"");
        sb.AppendLine("  exit 0");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("log \"================================================\"");
        sb.AppendLine("log \"Iniciando pos-instalacao do IsoForge\"");
        sb.AppendLine();
    }

    // ------------------------------------------------------------------
    // Abstracao do gerenciador de pacotes (apt / dnf / zypper / pacman)
    // ------------------------------------------------------------------
    static void PackageManagerHelpers(StringBuilder sb, OsFamily family)
    {
        sb.AppendLine("# ---------------- gerenciador de pacotes ----------------");
        switch (family)
        {
            case OsFamily.Debian:
                sb.AppendLine("export DEBIAN_FRONTEND=noninteractive");
                sb.AppendLine("pkg_refresh() { apt-get update -y; }");
                sb.AppendLine("pkg_install() { apt-get install -y \"$@\"; }");
                sb.AppendLine("pkg_remove() { apt-get purge -y \"$@\" 2>/dev/null || true; }");
                sb.AppendLine("pkg_upgrade() { apt-get upgrade -y; }");
                sb.AppendLine("pkg_exists() { apt-cache show \"$1\" >/dev/null 2>&1; }");
                break;
            case OsFamily.RedHat:
                sb.AppendLine("pkg_refresh() { dnf -y makecache || true; }");
                sb.AppendLine("pkg_install() { dnf install -y \"$@\"; }");
                sb.AppendLine("pkg_remove() { dnf remove -y \"$@\" 2>/dev/null || true; }");
                sb.AppendLine("pkg_upgrade() { dnf upgrade -y; }");
                sb.AppendLine("pkg_exists() { dnf info \"$1\" >/dev/null 2>&1; }");
                sb.AppendLine("# EPEL: alguns pacotes (p7zip) so existem la no Rocky/AlmaLinux.");
                sb.AppendLine("if ! rpm -q epel-release >/dev/null 2>&1 && grep -qiE 'rocky|almalinux|rhel' /etc/os-release 2>/dev/null; then");
                sb.AppendLine("  dnf install -y epel-release 2>/dev/null || true");
                sb.AppendLine("fi");
                break;
            case OsFamily.Suse:
                sb.AppendLine("pkg_refresh() { zypper --non-interactive --gpg-auto-import-keys refresh || true; }");
                sb.AppendLine("pkg_install() { zypper --non-interactive install --auto-agree-with-licenses \"$@\"; }");
                sb.AppendLine("pkg_remove() { zypper --non-interactive remove \"$@\" 2>/dev/null || true; }");
                sb.AppendLine("pkg_upgrade() { zypper --non-interactive update; }");
                sb.AppendLine("pkg_exists() { zypper --non-interactive info \"$1\" 2>/dev/null | grep -q '^Name'; }");
                break;
            default: // Arch
                sb.AppendLine("pkg_refresh() { pacman -Sy --noconfirm; }");
                sb.AppendLine("pkg_install() { pacman -S --noconfirm --needed \"$@\"; }");
                sb.AppendLine("pkg_remove() { pacman -Rns --noconfirm \"$@\" 2>/dev/null || true; }");
                sb.AppendLine("pkg_upgrade() { pacman -Syu --noconfirm; }");
                sb.AppendLine("pkg_exists() { pacman -Si \"$1\" >/dev/null 2>&1; }");
                break;
        }
        sb.AppendLine();
        sb.AppendLine("# Instala a PRIMEIRA alternativa que existir nos repositorios da distro.");
        sb.AppendLine("pkg_install_first() {");
        sb.AppendLine("  local p");
        sb.AppendLine("  for p in \"$@\"; do");
        sb.AppendLine("    if pkg_exists \"$p\"; then pkg_install \"$p\" && return 0; fi");
        sb.AppendLine("  done");
        sb.AppendLine("  log \"AVISO: nenhum pacote disponivel entre: $*\"");
        sb.AppendLine("  return 0");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("run pkg_refresh");
        sb.AppendLine();
    }

    // ------------------------------------------------------------------
    // Wi-Fi: perfil do NetworkManager (equivalente ao netsh wlan do Windows)
    // ------------------------------------------------------------------
    static void Wifi(StringBuilder sb, BuildConfig c)
    {
        if (!c.AutoConnectWifi || string.IsNullOrWhiteSpace(c.WifiSsid)) return;

        sb.AppendLine("# ---------------- Wi-Fi automatico ----------------");
        sb.AppendLine($"log \"Configurando a rede Wi-Fi {Ascii(c.WifiSsid)}...\"");
        sb.AppendLine("mkdir -p /etc/NetworkManager/system-connections");
        sb.AppendLine("WIFI_FILE=/etc/NetworkManager/system-connections/isoforge-wifi.nmconnection");
        sb.AppendLine("cat > \"$WIFI_FILE\" <<'ISOFORGE_WIFI_EOF'");
        sb.AppendLine("[connection]");
        sb.AppendLine("id=" + c.WifiSsid);
        sb.AppendLine("type=wifi");
        sb.AppendLine("autoconnect=true");
        sb.AppendLine();
        sb.AppendLine("[wifi]");
        sb.AppendLine("mode=infrastructure");
        sb.AppendLine("ssid=" + c.WifiSsid);
        if (!string.IsNullOrEmpty(c.WifiPassword))
        {
            sb.AppendLine();
            sb.AppendLine("[wifi-security]");
            sb.AppendLine("key-mgmt=wpa-psk");
            sb.AppendLine("psk=" + c.WifiPassword);
        }
        sb.AppendLine();
        sb.AppendLine("[ipv4]");
        sb.AppendLine("method=auto");
        sb.AppendLine();
        sb.AppendLine("[ipv6]");
        sb.AppendLine("method=auto");
        sb.AppendLine("ISOFORGE_WIFI_EOF");
        sb.AppendLine("chmod 600 \"$WIFI_FILE\"");
        sb.AppendLine("run nmcli connection reload");
        sb.AppendLine($"run nmcli connection up {Sh(c.WifiSsid)}");
        sb.AppendLine();
    }

    static void WaitForNetwork(StringBuilder sb, BuildConfig c)
    {
        // No Linux TODA instalacao de pacote precisa de rede: sempre espera.
        sb.AppendLine("# ---------------- espera por internet ----------------");
        sb.AppendLine("wait_for_internet() {");
        sb.AppendLine("  local tries=0");
        sb.AppendLine("  while [ $tries -lt 60 ]; do");
        sb.AppendLine("    if getent hosts deb.debian.org >/dev/null 2>&1 || ping -c1 -W2 1.1.1.1 >/dev/null 2>&1; then");
        sb.AppendLine("      log \"Internet disponivel.\"; return 0");
        sb.AppendLine("    fi");
        sb.AppendLine("    [ $tries -eq 0 ] && log \"Sem internet: aguardando conexao para instalar os programas...\"");
        sb.AppendLine("    tries=$((tries+1)); sleep 10");
        sb.AppendLine("  done");
        sb.AppendLine("  log \"AVISO: segui sem internet apos 10 minutos; alguns programas podem falhar.\"");
        sb.AppendLine("  return 0");
        sb.AppendLine("}");
        sb.AppendLine("wait_for_internet");
        sb.AppendLine();

        if (c.Linux.UpdateDuringInstall)
        {
            sb.AppendLine("log \"Atualizando os pacotes do sistema...\"");
            sb.AppendLine("run pkg_upgrade");
            sb.AppendLine();
        }
    }

    // ------------------------------------------------------------------
    // Aplicativos (mesmos cards do Windows, traduzidos para pacotes)
    // ------------------------------------------------------------------
    static void Apps(StringBuilder sb, BuildConfig c)
    {
        var recipes = SelectedRecipes(c);
        if (recipes.Count == 0) return;

        sb.AppendLine("# ---------------- aplicativos ----------------");
        sb.AppendLine($"log \"Serao instalados {recipes.Count} programa(s).\"");
        sb.AppendLine("run pkg_install curl ca-certificates gnupg");
        sb.AppendLine();

        for (int i = 0; i < recipes.Count; i++)
        {
            var r = recipes[i];
            sb.AppendLine($"log \"[{i + 1}/{recipes.Count}] {Ascii(r.DisplayName)}\"");
            if (!string.IsNullOrWhiteSpace(r.Note))
                sb.AppendLine($"log \"      {Ascii(r.Note)}\"");

            if (!string.IsNullOrWhiteSpace(r.RepoSetup))
            {
                sb.AppendLine($"log \"      adicionando o repositorio oficial...\"");
                sb.AppendLine("(");
                foreach (var line in r.RepoSetup.Replace("\r\n", "\n").Split('\n'))
                    sb.AppendLine("  " + line);
                sb.AppendLine(") || log \"AVISO: falha ao configurar o repositorio de " + Ascii(r.DisplayName) + "\"");
            }

            if (r.Packages.Length > 0)
                sb.AppendLine("run pkg_install " + string.Join(" ", r.Packages.Select(Sh)));
            if (r.FirstAvailable.Length > 0)
                sb.AppendLine("pkg_install_first " + string.Join(" ", r.FirstAvailable.Select(Sh)));
            if (!string.IsNullOrWhiteSpace(r.FlatpakId))
            {
                sb.AppendLine("ensure_flatpak");
                sb.AppendLine($"run flatpak install -y --noninteractive flathub {Sh(r.FlatpakId)}");
            }
            if (!string.IsNullOrWhiteSpace(r.PostInstall))
                sb.AppendLine(r.PostInstall);
            sb.AppendLine();
        }
    }

    /// <summary>Receitas Linux dos aplicativos escolhidos (ignora os sem equivalente).</summary>
    public static List<LinuxAppRecipe> SelectedRecipes(BuildConfig c)
    {
        var list = new List<LinuxAppRecipe>();
        foreach (var app in c.Apps)
        {
            if (!Enum.TryParse<AppId>(app.CatalogId, out var id)) continue;
            var recipe = LinuxAppCatalog.Recipe(id, c);
            if (recipe is { Installable: true }) list.Add(recipe);
        }
        return list;
    }

    // ------------------------------------------------------------------
    static void Flatpak(StringBuilder sb, BuildConfig c)
    {
        sb.AppendLine("# ---------------- flatpak ----------------");
        sb.AppendLine("ensure_flatpak() {");
        sb.AppendLine("  command -v flatpak >/dev/null 2>&1 || pkg_install flatpak");
        sb.AppendLine("  flatpak remote-add --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo 2>/dev/null || true");
        sb.AppendLine("}");
        if (c.Linux.EnableFlatpak)
        {
            sb.AppendLine("log \"Habilitando Flatpak + Flathub...\"");
            sb.AppendLine("ensure_flatpak");
        }
        sb.AppendLine();
    }

    static void ExtraPackages(StringBuilder sb, BuildConfig c)
    {
        var extras = (c.Linux.ExtraPackages ?? "").Split(new[] { ' ', ',', ';', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        if (extras.Length == 0) return;
        sb.AppendLine("# ---------------- pacotes extras ----------------");
        sb.AppendLine($"log \"Instalando {extras.Length} pacote(s) extra(s)...\"");
        sb.AppendLine("run pkg_install " + string.Join(" ", extras.Select(Sh)));
        sb.AppendLine();
    }

    static void Ssh(StringBuilder sb, BuildConfig c)
    {
        if (!c.Linux.InstallSshServer) return;
        sb.AppendLine("# ---------------- servidor SSH ----------------");
        sb.AppendLine("log \"Instalando e habilitando o servidor OpenSSH...\"");
        sb.AppendLine("pkg_install_first 'openssh-server' 'openssh'");
        sb.AppendLine("run systemctl enable --now sshd 2>/dev/null || systemctl enable --now ssh 2>/dev/null || true");
        sb.AppendLine();
    }

    // ------------------------------------------------------------------
    // Aparencia: papel de parede, tela de bloqueio, tema escuro, posicao do dock
    // ------------------------------------------------------------------
    public static bool HasAppearance(BuildConfig c) =>
        !string.IsNullOrWhiteSpace(c.WallpaperPath) ||
        !string.IsNullOrWhiteSpace(c.LockScreenPath) ||
        c.WindowsTheme != WindowsThemeMode.Default ||
        c.TaskbarAlign != TaskbarAlignment.Default;

    static void Appearance(StringBuilder sb, BuildConfig c)
    {
        if (!HasAppearance(c)) return;

        sb.AppendLine("# ---------------- aparencia ----------------");
        sb.AppendLine("log \"Aplicando aparencia padrao (papel de parede, tema, dock)...\"");
        sb.AppendLine("mkdir -p /usr/share/backgrounds/isoforge /etc/dconf/db/local.d /etc/dconf/profile");
        sb.AppendLine("cat > /etc/dconf/profile/user <<'ISOFORGE_PROFILE_EOF'");
        sb.AppendLine("user-db:user");
        sb.AppendLine("system-db:local");
        sb.AppendLine("ISOFORGE_PROFILE_EOF");
        sb.AppendLine();

        var wallpaperName = string.IsNullOrWhiteSpace(c.WallpaperPath) ? "" : Path.GetFileName(c.WallpaperPath);
        var lockName = string.IsNullOrWhiteSpace(c.LockScreenPath) ? "" : "lockscreen" + Path.GetExtension(c.LockScreenPath);

        if (wallpaperName.Length > 0 || lockName.Length > 0)
        {
            sb.AppendLine("cp -f \"$PAYLOAD\"/*.jpg \"$PAYLOAD\"/*.jpeg \"$PAYLOAD\"/*.png /usr/share/backgrounds/isoforge/ 2>/dev/null || true");
        }

        sb.AppendLine("cat > /etc/dconf/db/local.d/00-isoforge <<'ISOFORGE_DCONF_EOF'");
        if (wallpaperName.Length > 0)
        {
            sb.AppendLine("[org/gnome/desktop/background]");
            sb.AppendLine($"picture-uri='file:///usr/share/backgrounds/isoforge/{wallpaperName}'");
            sb.AppendLine($"picture-uri-dark='file:///usr/share/backgrounds/isoforge/{wallpaperName}'");
            sb.AppendLine("picture-options='zoom'");
            sb.AppendLine();
        }
        if (lockName.Length > 0)
        {
            sb.AppendLine("[org/gnome/desktop/screensaver]");
            sb.AppendLine($"picture-uri='file:///usr/share/backgrounds/isoforge/{lockName}'");
            sb.AppendLine("picture-options='zoom'");
            sb.AppendLine();
        }
        if (c.WindowsTheme != WindowsThemeMode.Default)
        {
            bool dark = c.WindowsTheme == WindowsThemeMode.Dark;
            sb.AppendLine("[org/gnome/desktop/interface]");
            sb.AppendLine($"color-scheme='{(dark ? "prefer-dark" : "prefer-light")}'");
            sb.AppendLine($"gtk-theme='{(dark ? "Adwaita-dark" : "Adwaita")}'");
            sb.AppendLine();
        }
        if (c.TaskbarAlign != TaskbarAlignment.Default)
        {
            // Centro = dock embaixo (padrao GNOME/Ubuntu); Esquerda = dock a esquerda (classico).
            var position = c.TaskbarAlign == TaskbarAlignment.Left ? "LEFT" : "BOTTOM";
            sb.AppendLine("[org/gnome/shell/extensions/dash-to-dock]");
            sb.AppendLine($"dock-position='{position}'");
            sb.AppendLine("extend-height=false");
            sb.AppendLine();
        }
        sb.AppendLine("ISOFORGE_DCONF_EOF");
        sb.AppendLine("run dconf update");

        // KDE (Plasma) nao usa dconf: aplica o papel de parede pelo utilitario proprio no login.
        if (wallpaperName.Length > 0)
        {
            sb.AppendLine("if command -v plasma-apply-wallpaperimage >/dev/null 2>&1; then");
            sb.AppendLine("  mkdir -p /etc/xdg/autostart");
            sb.AppendLine("  cat > /etc/xdg/autostart/isoforge-wallpaper.desktop <<'ISOFORGE_KDE_EOF'");
            sb.AppendLine("[Desktop Entry]");
            sb.AppendLine("Type=Application");
            sb.AppendLine("Name=IsoForge wallpaper");
            sb.AppendLine($"Exec=plasma-apply-wallpaperimage /usr/share/backgrounds/isoforge/{wallpaperName}");
            sb.AppendLine("X-GNOME-Autostart-enabled=false");
            sb.AppendLine("ISOFORGE_KDE_EOF");
            sb.AppendLine("fi");
        }
        sb.AppendLine();
    }

    // ------------------------------------------------------------------
    // VPN: os tuneis IPsec digitados viram conexoes strongSwan
    // ------------------------------------------------------------------
    public static bool HasVpn(BuildConfig c) => c.VpnTunnels.Count > 0;

    static void Vpn(StringBuilder sb, BuildConfig c)
    {
        if (!HasVpn(c)) return;

        sb.AppendLine("# ---------------- VPN IPsec (strongSwan) ----------------");
        sb.AppendLine($"log \"Configurando {c.VpnTunnels.Count} tunel(is) IPsec...\"");
        sb.AppendLine("pkg_install_first 'strongswan' 'strongswan-starter'");
        sb.AppendLine("mkdir -p /etc/ipsec.d");
        sb.AppendLine("cat > /etc/ipsec.d/isoforge.conf <<'ISOFORGE_IPSEC_EOF'");
        foreach (var t in c.VpnTunnels)
        {
            var name = Slug(t.Name);
            sb.AppendLine($"conn {name}");
            sb.AppendLine("    keyexchange=ikev1");
            sb.AppendLine("    auto=add");
            sb.AppendLine($"    right={t.RemoteGateway}");
            sb.AppendLine("    rightsubnet=0.0.0.0/0");
            sb.AppendLine("    leftsourceip=%config");
            sb.AppendLine("    leftauth=psk");
            sb.AppendLine("    rightauth=psk");
            if (c.VpnXAuth != VpnXAuthMode.Disabled)
            {
                sb.AppendLine("    leftauth2=xauth");
                if (c.VpnXAuth == VpnXAuthMode.Save && !string.IsNullOrWhiteSpace(c.XAuthUsername))
                    sb.AppendLine($"    xauth_identity={c.XAuthUsername}");
            }
            sb.AppendLine();
        }
        sb.AppendLine("ISOFORGE_IPSEC_EOF");
        sb.AppendLine("chmod 600 /etc/ipsec.d/isoforge.conf");
        sb.AppendLine("grep -q 'include /etc/ipsec.d/isoforge.conf' /etc/ipsec.conf 2>/dev/null || \\");
        sb.AppendLine("  echo 'include /etc/ipsec.d/isoforge.conf' >> /etc/ipsec.conf");

        sb.AppendLine("cat > /etc/ipsec.d/isoforge.secrets <<'ISOFORGE_SECRETS_EOF'");
        foreach (var t in c.VpnTunnels)
            sb.AppendLine($"{t.RemoteGateway} : PSK \"{t.PresharedKey.Replace("\"", "")}\"");
        if (c.VpnXAuth == VpnXAuthMode.Save && !string.IsNullOrWhiteSpace(c.XAuthUsername))
            sb.AppendLine($"{c.XAuthUsername} : XAUTH \"{(c.XAuthPassword ?? "").Replace("\"", "")}\"");
        sb.AppendLine("ISOFORGE_SECRETS_EOF");
        sb.AppendLine("chmod 600 /etc/ipsec.d/isoforge.secrets");
        sb.AppendLine("grep -q 'include /etc/ipsec.d/isoforge.secrets' /etc/ipsec.secrets 2>/dev/null || \\");
        sb.AppendLine("  echo 'include /etc/ipsec.d/isoforge.secrets' >> /etc/ipsec.secrets");
        sb.AppendLine("run systemctl enable strongswan-starter 2>/dev/null || systemctl enable strongswan 2>/dev/null || true");
        sb.AppendLine();
    }

    // ------------------------------------------------------------------
    // Otimizacao (equivalente ao debloat do Windows)
    // ------------------------------------------------------------------
    public static bool HasDebloat(BuildConfig c) =>
        c.Linux.RemoveDefaultGames || c.Linux.DisableTelemetry || c.Linux.RemoveSnap || c.Linux.DisableMotdAds;

    static void Debloat(StringBuilder sb, BuildConfig c, OsFamily family)
    {
        if (!HasDebloat(c)) return;

        sb.AppendLine("# ---------------- otimizacao ----------------");
        if (c.Linux.RemoveDefaultGames)
        {
            sb.AppendLine("log \"Removendo jogos e aplicativos de fabrica...\"");
            var games = family switch
            {
                OsFamily.Debian => new[] { "gnome-games", "aisleriot", "gnome-mahjongg", "gnome-mines", "gnome-sudoku", "gnome-2048", "quadrapassel", "hitori", "iagno", "lightsoff", "swell-foop", "tali", "four-in-a-row", "five-or-more" },
                OsFamily.RedHat => new[] { "gnome-games", "aisleriot", "gnome-mahjongg", "gnome-mines", "gnome-sudoku", "quadrapassel", "iagno", "lightsoff", "swell-foop", "tali" },
                OsFamily.Suse => new[] { "gnome-games", "aisleriot", "gnome-mahjongg", "gnome-mines", "gnome-sudoku" },
                _ => new[] { "gnome-games" }
            };
            sb.AppendLine("run pkg_remove " + string.Join(" ", games.Select(Sh)));
        }
        if (c.Linux.DisableTelemetry)
        {
            sb.AppendLine("log \"Desativando coleta de dados e relatorios de erro...\"");
            sb.AppendLine("run pkg_remove 'popularity-contest' 'ubuntu-report' 'whoopsie' 'apport'");
            sb.AppendLine("systemctl disable --now apport.service whoopsie.service 2>/dev/null || true");
            sb.AppendLine("[ -f /etc/default/apport ] && sed -i 's/^enabled=.*/enabled=0/' /etc/default/apport");
            sb.AppendLine("mkdir -p /etc/dnf && grep -q '^countme' /etc/dnf/dnf.conf 2>/dev/null || \\");
            sb.AppendLine("  { [ -f /etc/dnf/dnf.conf ] && echo 'countme=false' >> /etc/dnf/dnf.conf; }");
        }
        if (c.Linux.RemoveSnap && family == OsFamily.Debian)
        {
            sb.AppendLine("log \"Removendo o snapd (prioriza pacotes .deb nativos)...\"");
            sb.AppendLine("snap list 2>/dev/null | awk 'NR>1 {print $1}' | while read -r s; do snap remove --purge \"$s\" 2>/dev/null || true; done");
            sb.AppendLine("systemctl disable --now snapd.service snapd.socket snapd.seeded.service 2>/dev/null || true");
            sb.AppendLine("run pkg_remove 'snapd'");
            sb.AppendLine("rm -rf /var/cache/snapd /snap /root/snap 2>/dev/null || true");
            sb.AppendLine("cat > /etc/apt/preferences.d/nosnap <<'ISOFORGE_NOSNAP_EOF'");
            sb.AppendLine("Package: snapd");
            sb.AppendLine("Pin: release a=*");
            sb.AppendLine("Pin-Priority: -10");
            sb.AppendLine("ISOFORGE_NOSNAP_EOF");
        }
        if (c.Linux.DisableMotdAds)
        {
            sb.AppendLine("log \"Desativando as mensagens promocionais do MOTD...\"");
            sb.AppendLine("[ -f /etc/default/motd-news ] && sed -i 's/^ENABLED=.*/ENABLED=0/' /etc/default/motd-news");
            sb.AppendLine("systemctl disable --now motd-news.timer 2>/dev/null || true");
            sb.AppendLine("chmod -x /etc/update-motd.d/* 2>/dev/null || true");
            sb.AppendLine("[ -f /etc/apt/apt.conf.d/20apt-esm-hook.conf ] && : > /etc/apt/apt.conf.d/20apt-esm-hook.conf");
        }
        sb.AppendLine();
    }

    // ------------------------------------------------------------------
    // Selecao de unidade: hostname = PREFIXO + numero de serie do BIOS
    // ------------------------------------------------------------------
    static void UnitSelection(StringBuilder sb, BuildConfig c)
    {
        if (!c.UseUnitSelection || c.Units.Count == 0) return;

        sb.AppendLine("# ---------------- selecao de unidade ----------------");
        sb.AppendLine("log \"Instalando a tela de selecao de unidade (nome = prefixo + nº de serie)...\"");
        sb.AppendLine("pkg_install_first 'zenity' 'gnome-shell' >/dev/null 2>&1 || true");
        sb.AppendLine("pkg_install_first 'dmidecode' >/dev/null 2>&1 || true");
        sb.AppendLine("mkdir -p /usr/local/sbin /usr/local/bin /etc/xdg/autostart /etc/sudoers.d /etc/isoforge");
        sb.AppendLine();

        // Lista de unidades (rotulo|prefixo), lida pelos dois scripts.
        sb.AppendLine("cat > /etc/isoforge/units.conf <<'ISOFORGE_UNITS_EOF'");
        foreach (var u in c.Units)
            sb.AppendLine($"{Ascii(u.Name)}|{Ascii(u.Prefix)}");
        sb.AppendLine("ISOFORGE_UNITS_EOF");
        sb.AppendLine();

        sb.AppendLine($"cat > /usr/local/sbin/{SetHostnameFileName} <<'ISOFORGE_SETHOST_EOF'");
        sb.AppendLine(SetHostnameScript());
        sb.AppendLine("ISOFORGE_SETHOST_EOF");
        sb.AppendLine($"chmod 750 /usr/local/sbin/{SetHostnameFileName}");
        sb.AppendLine();

        sb.AppendLine($"cat > /usr/local/bin/{SelectUnitFileName} <<'ISOFORGE_SELUNIT_EOF'");
        sb.AppendLine(SelectUnitScript(c));
        sb.AppendLine("ISOFORGE_SELUNIT_EOF");
        sb.AppendLine($"chmod 755 /usr/local/bin/{SelectUnitFileName}");
        sb.AppendLine();

        // Permite so este comando sem senha (o script valida o prefixo recebido).
        sb.AppendLine("cat > /etc/sudoers.d/isoforge-unit <<'ISOFORGE_SUDO_EOF'");
        sb.AppendLine($"ALL ALL=(root) NOPASSWD: /usr/local/sbin/{SetHostnameFileName}");
        sb.AppendLine("ISOFORGE_SUDO_EOF");
        sb.AppendLine("chmod 440 /etc/sudoers.d/isoforge-unit");
        sb.AppendLine();

        // Sessao grafica: autostart no primeiro login.
        sb.AppendLine("cat > /etc/xdg/autostart/isoforge-select-unit.desktop <<'ISOFORGE_AUTOSTART_EOF'");
        sb.AppendLine("[Desktop Entry]");
        sb.AppendLine("Type=Application");
        sb.AppendLine("Name=IsoForge - selecao de unidade");
        sb.AppendLine($"Exec=/usr/local/bin/{SelectUnitFileName}");
        sb.AppendLine("Terminal=false");
        sb.AppendLine("NoDisplay=true");
        sb.AppendLine("X-GNOME-Autostart-enabled=true");
        sb.AppendLine("ISOFORGE_AUTOSTART_EOF");
        sb.AppendLine();

        // Sem interface grafica (servidor): roda no primeiro login do console.
        sb.AppendLine("cat > /etc/profile.d/isoforge-select-unit.sh <<'ISOFORGE_PROFILE_UNIT_EOF'");
        sb.AppendLine("# IsoForge: selecao de unidade no primeiro login de console (sem ambiente grafico).");
        sb.AppendLine("if [ -z \"${DISPLAY:-}\" ] && [ -z \"${WAYLAND_DISPLAY:-}\" ] && [ ! -f /var/lib/isoforge/unit.done ] && [ -t 0 ]; then");
        sb.AppendLine($"  /usr/local/bin/{SelectUnitFileName}");
        sb.AppendLine("fi");
        sb.AppendLine("ISOFORGE_PROFILE_UNIT_EOF");
        sb.AppendLine();
    }

    /// <summary>Script root que aplica o hostname (prefixo + nº de série do BIOS).</summary>
    public static string SetHostnameScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("# IsoForge: define o hostname como PREFIXO + numero de serie do equipamento.");
        sb.AppendLine("set -eu");
        sb.AppendLine("PREFIX=\"${1:-}\"");
        sb.AppendLine("# So aceita letras, numeros e hifen (o prefixo vem da tela de selecao).");
        sb.AppendLine("if ! printf '%s' \"$PREFIX\" | grep -Eq '^[A-Za-z0-9-]{1,20}$'; then");
        sb.AppendLine("  echo \"IsoForge: prefixo invalido: $PREFIX\" >&2; exit 1");
        sb.AppendLine("fi");
        sb.AppendLine("SERIAL=\"$(dmidecode -s system-serial-number 2>/dev/null | head -n1 | tr -cd 'A-Za-z0-9')\"");
        sb.AppendLine("[ -z \"$SERIAL\" ] && SERIAL=\"$(cat /sys/class/dmi/id/product_serial 2>/dev/null | tr -cd 'A-Za-z0-9')\"");
        sb.AppendLine("[ -z \"$SERIAL\" ] && SERIAL=\"$(head -c4 /dev/urandom | od -An -tx1 | tr -d ' \\n')\"");
        sb.AppendLine("NAME=\"$(printf '%s%s' \"$PREFIX\" \"$SERIAL\" | cut -c1-63)\"");
        sb.AppendLine("hostnamectl set-hostname \"$NAME\"");
        sb.AppendLine("if grep -q '^127.0.1.1' /etc/hosts; then");
        sb.AppendLine("  sed -i \"s/^127.0.1.1.*/127.0.1.1\\t$NAME/\" /etc/hosts");
        sb.AppendLine("else");
        sb.AppendLine("  printf '127.0.1.1\\t%s\\n' \"$NAME\" >> /etc/hosts");
        sb.AppendLine("fi");
        sb.AppendLine("mkdir -p /var/lib/isoforge");
        sb.AppendLine("printf '%s\\n' \"$NAME\" > /var/lib/isoforge/unit.done");
        sb.AppendLine("rm -f /etc/xdg/autostart/isoforge-select-unit.desktop /etc/profile.d/isoforge-select-unit.sh");
        sb.AppendLine("echo \"$NAME\"");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Tela de escolha da unidade (zenity no gráfico, texto no console).</summary>
    public static string SelectUnitScript(BuildConfig c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("# IsoForge: pergunta a unidade e renomeia a maquina (prefixo + numero de serie).");
        sb.AppendLine("set -u");
        sb.AppendLine("[ -f /var/lib/isoforge/unit.done ] && exit 0");
        sb.AppendLine("UNITS_FILE=/etc/isoforge/units.conf");
        sb.AppendLine("[ -f \"$UNITS_FILE\" ] || exit 0");
        sb.AppendLine();
        sb.AppendLine("PREFIX=\"\"");
        sb.AppendLine("if [ -n \"${DISPLAY:-}${WAYLAND_DISPLAY:-}\" ] && command -v zenity >/dev/null 2>&1; then");
        sb.AppendLine("  ARGS=()");
        sb.AppendLine("  while IFS='|' read -r nome prefixo; do");
        sb.AppendLine("    [ -z \"$nome\" ] && continue");
        sb.AppendLine("    ARGS+=(\"$prefixo\" \"$nome\")");
        sb.AppendLine("  done < \"$UNITS_FILE\"");
        sb.AppendLine("  PREFIX=$(zenity --list --title='IsoForge - Selecione a unidade' \\");
        sb.AppendLine("    --text='Escolha a unidade desta maquina (define o nome do computador):' \\");
        sb.AppendLine("    --column='Prefixo' --column='Unidade' --hide-column=1 --print-column=1 \\");
        sb.AppendLine("    --width=460 --height=380 \"${ARGS[@]}\" 2>/dev/null)");
        sb.AppendLine("else");
        sb.AppendLine("  echo ''");
        sb.AppendLine("  echo '=============================================='");
        sb.AppendLine("  echo ' IsoForge - selecione a unidade desta maquina'");
        sb.AppendLine("  echo '=============================================='");
        sb.AppendLine("  i=0; NOMES=(); PREFIXOS=()");
        sb.AppendLine("  while IFS='|' read -r nome prefixo; do");
        sb.AppendLine("    [ -z \"$nome\" ] && continue");
        sb.AppendLine("    i=$((i+1)); NOMES+=(\"$nome\"); PREFIXOS+=(\"$prefixo\")");
        sb.AppendLine("    printf '  %d) %s (%s)\\n' \"$i\" \"$nome\" \"$prefixo\"");
        sb.AppendLine("  done < \"$UNITS_FILE\"");
        sb.AppendLine("  printf 'Numero da unidade: '");
        sb.AppendLine("  read -r escolha");
        sb.AppendLine("  case \"$escolha\" in");
        sb.AppendLine("    ''|*[!0-9]*) exit 0 ;;");
        sb.AppendLine("  esac");
        sb.AppendLine("  [ \"$escolha\" -ge 1 ] 2>/dev/null && [ \"$escolha\" -le \"${#PREFIXOS[@]}\" ] && PREFIX=\"${PREFIXOS[$((escolha-1))]}\"");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("[ -z \"$PREFIX\" ] && exit 0");
        sb.AppendLine($"NOVO=$(sudo -n /usr/local/sbin/{SetHostnameFileName} \"$PREFIX\" 2>/dev/null)");
        sb.AppendLine("[ -z \"$NOVO\" ] && exit 1");
        if (c.SandboxTest)
        {
            sb.AppendLine("# Modo de teste: nao reinicia (a maquina real reiniciaria aqui).");
            sb.AppendLine("echo \"IsoForge: nome definido para $NOVO (teste: reinicio pulado).\"");
        }
        else
        {
            sb.AppendLine("if command -v zenity >/dev/null 2>&1 && [ -n \"${DISPLAY:-}${WAYLAND_DISPLAY:-}\" ]; then");
            sb.AppendLine("  zenity --info --title='IsoForge' --width=380 \\");
            sb.AppendLine("    --text=\"Nome do computador definido: $NOVO\\n\\nA maquina vai reiniciar para aplicar.\" 2>/dev/null");
            sb.AppendLine("else");
            sb.AppendLine("  echo \"IsoForge: nome definido para $NOVO. Reiniciando para aplicar...\"; sleep 3");
            sb.AppendLine("fi");
            sb.AppendLine("sudo -n systemctl reboot 2>/dev/null || systemctl reboot 2>/dev/null || true");
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------
    static void PostScript(StringBuilder sb, BuildConfig c)
    {
        if (string.IsNullOrWhiteSpace(c.PostScriptPath)) return;
        var name = Path.GetFileName(c.PostScriptPath);
        sb.AppendLine("# ---------------- script personalizado ----------------");
        sb.AppendLine($"log \"Executando o script personalizado {Ascii(name)}...\"");
        sb.AppendLine($"if [ -f \"$PAYLOAD/{name}\" ]; then");
        sb.AppendLine($"  chmod +x \"$PAYLOAD/{name}\" 2>/dev/null || true");
        sb.AppendLine($"  bash \"$PAYLOAD/{name}\" || log \"AVISO: o script personalizado terminou com erro.\"");
        sb.AppendLine("fi");
        sb.AppendLine();
    }

    static void Report(StringBuilder sb, BuildConfig c)
    {
        if (!c.GenerateReport) return;
        sb.AppendLine("# ---------------- relatorio ----------------");
        sb.AppendLine("log \"Gerando o relatorio de provisionamento...\"");
        sb.AppendLine($"bash \"$PAYLOAD/{LinuxReportGenerator.FileName}\" || log \"AVISO: falha ao gerar o relatorio.\"");
        sb.AppendLine();
    }

    static void Footer(StringBuilder sb, BuildConfig c)
    {
        sb.AppendLine("# ---------------- conclusao ----------------");
        sb.AppendLine("date > \"$DONE_MARKER\"");
        sb.AppendLine("log \"Pos-instalacao concluida. Log completo em $LOGFILE\"");
        sb.AppendLine("systemctl disable isoforge-firstboot.service 2>/dev/null || true");
        sb.AppendLine("exit 0");
    }

    // ------------------------------------------------------------------
    /// <summary>Unidade systemd que dispara o script no primeiro boot.</summary>
    public static string FirstBootService()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Unit]");
        sb.AppendLine("Description=IsoForge - pos-instalacao (primeiro boot)");
        sb.AppendLine("After=network-online.target");
        sb.AppendLine("Wants=network-online.target");
        sb.AppendLine($"ConditionPathExists=!{DoneMarker}");
        sb.AppendLine();
        sb.AppendLine("[Service]");
        sb.AppendLine("Type=oneshot");
        sb.AppendLine("RemainAfterExit=yes");
        sb.AppendLine($"ExecStart=/usr/bin/env bash {PayloadDirOnDisk}/{FileName}");
        sb.AppendLine("TimeoutStartSec=3600");
        sb.AppendLine("StandardOutput=journal+console");
        sb.AppendLine("StandardError=journal+console");
        sb.AppendLine();
        sb.AppendLine("[Install]");
        sb.AppendLine("WantedBy=multi-user.target");
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    /// <summary>Remove acentuação para manter os scripts em ASCII puro.</summary>
    public static string Ascii(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch <= 126 ? ch : '?');
        }
        return sb.ToString().Replace("\"", "'");
    }

    /// <summary>Nome seguro para conexões/arquivos (só letras, números e hífen).</summary>
    public static string Slug(string? text)
    {
        var ascii = Ascii(text).ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var ch in ascii)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var s = sb.ToString().Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return string.IsNullOrEmpty(s) ? "isoforge" : s;
    }
}
