using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using IsoForge.Core;
using IsoForge.Core.Linux;
using IsoForge.Models;

namespace IsoForge.SmokeTest;

/// <summary>
/// Testes de fumaça do suporte a Linux: identificação da ISO, arquivos de resposta de cada
/// família (autoinstall, preseed, kickstart, AutoYaST, archinstall), script de pós-instalação,
/// catálogo de aplicativos, ajuste dos bootloaders e extração das imagens de boot El Torito.
/// Tudo offline, sem precisar de ISO real nem da interface.
/// </summary>
public static class LinuxTests
{
    public static void Run(Action<bool, string> check, string outDir)
    {
        Console.WriteLine();
        Console.WriteLine("==================== LINUX ====================");

        Crypt(check);
        IsoDetection(check, outDir);
        AppCatalog(check);
        Ubuntu(check, outDir);
        Debian(check, outDir);
        RedHat(check, outDir);
        Suse(check, outDir);
        Arch(check, outDir);
        PostInstall(check);
        UnitSelection(check);
        AnswerFiles(check);
        Bootloader(check);
        ElToritoTests(check, outDir);
        Validation(check);
        Payload(check, outDir);
    }

    // ------------------------------------------------------------------
    /// <summary>Configuração típica de uma distro, usada como base nos testes.</summary>
    static BuildConfig Cfg(TargetOs os, params AppId[] apps)
    {
        var c = new BuildConfig
        {
            Os = os,
            UserName = "suporte",
            Password = "S3nh@Forte!",
            IsAdministrator = true,
            ComputerName = "TESTE-PC01",
            OfficeLanguage = "pt-br",
            GenerateReport = true
        };
        c.Linux.FullName = "Suporte TI";
        foreach (var id in apps)
        {
            var recipe = LinuxAppCatalog.Recipe(id, c);
            c.Apps.Add(new AppEntry
            {
                Name = recipe?.DisplayName ?? id.ToString(),
                CatalogId = id.ToString(),
                Kind = id == AppId.OfficeOdt ? AppKind.Office : AppKind.Generic
            });
        }
        return c;
    }

    // ------------------------------------------------------------------
    // Senha cifrada (SHA-512 crypt)
    // ------------------------------------------------------------------
    static void Crypt(Action<bool, string> check)
    {
        // Vetores oficiais da especificação do SHA-crypt (Ulrich Drepper).
        check(UnixCrypt.Sha512("Hello world!", "saltstring") ==
              "$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1",
              "crypt: vetor oficial 1 (sal simples, 5000 rodadas)");
        check(UnixCrypt.Sha512("Hello world!", "rounds=10000$saltstringsaltstring") ==
              "$6$rounds=10000$saltstringsaltst$OW1/O6BYHV6BcXZu8QVeXbDWra3Oeqh0sbHbbMCVNSnCM/UrjmM0Dp8vOuZeHBy/YTBmSK6H9qs/y3RnOaw5v.",
              "crypt: vetor oficial 2 (sal truncado em 16, 10000 rodadas)");
        check(UnixCrypt.Sha512("This is just a test", "rounds=5000$toolongsaltstring") ==
              "$6$rounds=5000$toolongsaltstrin$lQ8jolhgVRVhY4b5pZKaysCLi0QBxGoNeKQzQ3glMhwllF7oGDZxUhx1yxdYcz/e1JSbq3y6JMxxl8audkUEm0",
              "crypt: vetor oficial 3");
        check(UnixCrypt.Sha512("we have a short salt string but not a short password", "rounds=77777$short") ==
              "$6$rounds=77777$short$WuQyW2YR.hBNpjjRhpYD/ifIw05xdfeEyQoMxIXbkvr0gge1a1x3yRULJ5CCaUeOxFmtlcGZelFl5CxtgfiAc0",
              "crypt: vetor oficial 4 (77777 rodadas)");

        var a = UnixCrypt.Sha512("mesma-senha");
        var b = UnixCrypt.Sha512("mesma-senha");
        check(a != b && a.StartsWith("$6$") && a.Split('$').Length == 4,
              "crypt: sal aleatório a cada chamada (hashes diferentes, formato $6$sal$hash)");
        check(UnixCrypt.Sha512("") == "", "crypt: senha vazia devolve vazio (conta sem senha)");
    }

    // ------------------------------------------------------------------
    // Identificação da ISO + correção automática do sistema
    // ------------------------------------------------------------------
    static void IsoDetection(Action<bool, string> check, string outDir)
    {
        var dir = Path.Combine(outDir, "isos");
        Directory.CreateDirectory(dir);

        string Iso(string name, string label, params string[] rootEntries)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, FakeIso.Build(label, rootEntries));
            return path;
        }

        void Detects(string fileName, string label, TargetOs expected, string what, params string[] entries)
            => check(IsoInspector.Identify(Iso(fileName, label, entries))?.Os == expected, "ISO: " + what);

        Detects("ubuntu.iso", "Ubuntu 24.04.1 LTS amd64", TargetOs.Ubuntu, "Ubuntu reconhecido pelo rótulo");
        Detects("ubuntu-server.iso", "Ubuntu-Server 24.04.1 LTS amd64", TargetOs.UbuntuServer, "Ubuntu Server distinguido do Desktop");
        Detects("debian.iso", "Debian 12.5.0 amd64 n", TargetOs.Debian, "Debian reconhecido");
        Detects("mint.iso", "Linux Mint 21.3 Cinnamon 64-bit", TargetOs.LinuxMint, "Linux Mint reconhecido");
        Detects("fedora.iso", "Fedora-WS-Live-40-1-14", TargetOs.Fedora, "Fedora reconhecido");
        Detects("rocky.iso", "Rocky-9-4-x86_64-dvd", TargetOs.RockyLinux, "Rocky Linux reconhecido");
        Detects("alma.iso", "AlmaLinux-9-4-x86_64-dvd", TargetOs.AlmaLinux, "AlmaLinux reconhecido");
        Detects("suse.iso", "openSUSE-Leap-15.6-DVD-x86_64", TargetOs.OpenSuse, "openSUSE reconhecido");
        Detects("arch.iso", "ARCH_202406", TargetOs.ArchLinux, "Arch Linux reconhecido");
        Detects("win11.iso", "CCCOMA_X64FRE_PT-BR_DV9", TargetOs.Windows11, "Windows 11 reconhecido pelo rótulo oficial");

        // Sem rótulo útil: cai na estrutura do diretório raiz.
        Detects("semrotulo-win.iso", "VOLUME", TargetOs.Windows11, "Windows reconhecido por sources/ + setup.exe",
            "SOURCES", "BOOT", "SETUP.EXE", "EFI");
        Detects("semrotulo-ubuntu.iso", "VOLUME", TargetOs.Ubuntu, "Ubuntu reconhecido por casper/",
            "CASPER", "EFI", "BOOT", "POOL");
        Detects("semrotulo-debian.iso", "VOLUME", TargetOs.Debian, "Debian reconhecido por install.amd + dists/",
            "INSTALL.AMD", "DISTS", "POOL");
        Detects("semrotulo-fedora.iso", "VOLUME", TargetOs.Fedora, "Anaconda reconhecido por images/ + LiveOS/",
            "IMAGES", "LIVEOS", "EFI");
        Detects("semrotulo-arch.iso", "VOLUME", TargetOs.ArchLinux, "Arch reconhecido por arch/", "ARCH", "EFI", "BOOT");

        // Distro conhecida mas ainda não suportada.
        var pop = IsoInspector.Identify(Iso("pop.iso", "Pop_OS 22.04 amd64"));
        check(pop is { Os: null, Supported: false } && pop.UnsupportedName == "Pop!_OS",
              "ISO: distro conhecida porém não suportada é sinalizada (Pop!_OS)");

        var desconhecida = IsoInspector.Identify(Iso("qualquer.iso", "MEU_BACKUP_2024"));
        check(desconhecida is { Os: null, Supported: true }, "ISO: imagem desconhecida não é confundida com nenhum sistema");

        var ubuntuId = IsoInspector.Identify(Iso("ubuntu2.iso", "Ubuntu 24.04.1 LTS amd64"));
        check(ubuntuId?.Version == "24.04.1", "ISO: versão extraída do rótulo");

        // Correção automática: a ISO manda.
        check(IsoInspector.Mismatch(TargetOs.Windows11, ubuntuId) == TargetOs.Ubuntu,
              "ISO: divergência detectada (Windows selecionado × ISO do Ubuntu) devolve a correção");
        check(IsoInspector.Mismatch(TargetOs.Ubuntu, ubuntuId) == null,
              "ISO: sem divergência quando o sistema já está certo");
        check(IsoInspector.Mismatch(TargetOs.Ubuntu, IsoInspector.Identify(Iso("u3.iso", "Ubuntu 22.04 LTS amd64"))) == null,
              "ISO: Ubuntu Desktop × Server não gera troca indevida quando o rótulo não diz 'Server'");
        check(IsoInspector.Mismatch(TargetOs.Fedora, desconhecida) == null,
              "ISO: imagem não identificada nunca força troca de sistema");

        // Windows 10 e 11 compartilham o rótulo oficial: sem marca de versão, respeita a escolha.
        var winGenerica = IsoInspector.Identify(Iso("win-generica.iso", "CCCOMA_X64FRE_PT-BR_DV9"));
        check(IsoInspector.Mismatch(TargetOs.Windows10, winGenerica) == null,
              "ISO: rótulo genérico do Windows não troca a versão escolhida (10 x 11 usam o mesmo)");
        check(IsoInspector.Mismatch(TargetOs.Windows10, IsoInspector.Identify(Iso("w11.iso", "WIN11_23H2_PTBR"))) == TargetOs.Windows11,
              "ISO: rótulo que diz a versão corrige a escolha (Windows 10 -> 11)");
        check(IsoInspector.Mismatch(TargetOs.Windows10, ubuntuId) == TargetOs.Ubuntu,
              "ISO: Windows selecionado com ISO Linux é sempre corrigido");
        check(IsoInspector.Identify(Path.Combine(dir, "nao-existe.iso")) == null,
              "ISO: arquivo inexistente devolve null sem lançar");
    }

    // ------------------------------------------------------------------
    // Catálogo de aplicativos (os mesmos do Windows, na versão de cada distro)
    // ------------------------------------------------------------------
    static void AppCatalog(Action<bool, string> check)
    {
        LinuxAppRecipe R(TargetOs os, AppId id) => LinuxAppCatalog.Recipe(id, Cfg(os))!;

        // Todos os apps do catálogo do Windows têm equivalente em toda distro suportada.
        var faltando = new List<string>();
        foreach (var os in OsCatalog.All.Where(o => o.IsLinux))
            foreach (var id in LinuxAppCatalog.Supported)
            {
                var r = LinuxAppCatalog.Recipe(id, Cfg(os.Id));
                if (r is not { Installable: true }) faltando.Add($"{os.Name}/{id}");
            }
        check(faltando.Count == 0, "apps: todos os programas do Windows têm equivalente em todas as distros" +
                                    (faltando.Count == 0 ? "" : " — faltou: " + string.Join(", ", faltando)));

        var chromeDeb = R(TargetOs.Ubuntu, AppId.Chrome);
        check(chromeDeb.Packages.Contains("google-chrome-stable") && chromeDeb.RepoSetup.Contains("dl.google.com/linux/chrome/deb"),
              "apps: Chrome no Ubuntu vem do repositório oficial do Google");
        check(R(TargetOs.Fedora, AppId.Chrome).RepoSetup.Contains("yum.repos.d/google-chrome.repo"),
              "apps: Chrome no Fedora usa o repositório .rpm oficial");
        check(R(TargetOs.ArchLinux, AppId.Chrome).Packages.Contains("chromium"),
              "apps: no Arch o Chrome (que só existe no AUR) vira Chromium do repositório oficial");

        var anydeskArch = R(TargetOs.ArchLinux, AppId.AnyDesk);
        check(anydeskArch.FlatpakId == "com.anydesk.Anydesk" && anydeskArch.Installable,
              "apps: AnyDesk no Arch é instalado pelo Flathub (oficial)");
        check(R(TargetOs.Debian, AppId.AnyDesk).RepoSetup.Contains("deb.anydesk.com"),
              "apps: AnyDesk no Debian vem do repositório oficial da AnyDesk");

        check(R(TargetOs.Ubuntu, AppId.SevenZip).FirstAvailable.SequenceEqual(new[] { "7zip", "p7zip-full" }),
              "apps: 7-Zip tenta '7zip' e cai para 'p7zip-full' nas versões antigas");

        var office = R(TargetOs.Ubuntu, AppId.OfficeOdt);
        check(office.Packages.Contains("libreoffice") && office.Packages.Contains("libreoffice-l10n-pt-br"),
              "apps: Office 365 vira LibreOffice + pacote de idioma pt-BR");
        var cfgEn = Cfg(TargetOs.Fedora); cfgEn.OfficeLanguage = "en-us";
        check(LinuxAppCatalog.Recipe(AppId.OfficeOdt, cfgEn)!.Packages.Length == 1,
              "apps: em inglês o LibreOffice dispensa pacote de idioma");
        check(R(TargetOs.ArchLinux, AppId.OfficeOdt).Packages.Contains("libreoffice-fresh-pt-br"),
              "apps: no Arch o pacote de idioma segue a nomenclatura do libreoffice-fresh");

        check(R(TargetOs.Ubuntu, AppId.AdobeReader).Note.Contains("descontinuou") &&
              R(TargetOs.Ubuntu, AppId.AdobeReader).Packages.Contains("evince"),
              "apps: Adobe Reader (descontinuado no Linux) vira o leitor de PDF do ambiente, com aviso");
        var kde = Cfg(TargetOs.OpenSuse); kde.Linux.Desktop = LinuxDesktop.Kde;
        check(LinuxAppCatalog.Recipe(AppId.AdobeReader, kde)!.Packages.Contains("okular"),
              "apps: em KDE o leitor de PDF é o Okular");

        check(R(TargetOs.Debian, AppId.NotepadPlus).Packages.Contains("geany"),
              "apps: Notepad++ (exclusivo do Windows) vira Geany");
        check(R(TargetOs.Ubuntu, AppId.VcRedist).Packages.Contains("build-essential") &&
              R(TargetOs.Fedora, AppId.VcRedist).Packages.Contains("gcc-c++"),
              "apps: Visual C++ vira a toolchain de compilação da distro");

        check(R(TargetOs.Ubuntu, AppId.FortiClient).RepoSetup.Contains("repo.fortinet.com") &&
              R(TargetOs.Ubuntu, AppId.FortiClient).Packages.Contains("forticlient"),
              "apps: FortiClient vem do repositório oficial da Fortinet no Ubuntu");
        check(R(TargetOs.Debian, AppId.FortiClient).RepoSetup.Contains("/debian/"),
              "apps: FortiClient usa o caminho do Debian quando a distro é Debian");
        check(R(TargetOs.OpenSuse, AppId.FortiClient).Packages.Contains("openfortivpn"),
              "apps: sem pacote da Fortinet, o openSUSE recebe o openfortivpn");

        check(R(TargetOs.Ubuntu, AppId.Firefox).RepoSetup.Contains("packages.mozilla.org") &&
              R(TargetOs.Ubuntu, AppId.Firefox).RepoSetup.Contains("Pin-Priority"),
              "apps: Firefox no Ubuntu vem como .deb da Mozilla (evita o snap), com pin do apt");
        check(R(TargetOs.OpenSuse, AppId.Firefox).Packages.Contains("MozillaFirefox"),
              "apps: no openSUSE o pacote do Firefox tem outro nome");

        check(LinuxAppCatalog.Recipe(AppId.FortiClientLatest, Cfg(TargetOs.Ubuntu))!.Packages.Contains("forticlient"),
              "apps: FortiClient 'mais recente' e 7.4.1 são o mesmo pacote no Linux");
    }

    // ------------------------------------------------------------------
    // Ubuntu — autoinstall (cloud-init)
    // ------------------------------------------------------------------
    static void Ubuntu(Action<bool, string> check, string outDir)
    {
        var c = Cfg(TargetOs.Ubuntu, AppId.OfficeOdt, AppId.Chrome, AppId.AnyDesk);
        c.Linux.InstallSshServer = true;
        c.Linux.DiskMode = LinuxDiskMode.EntireDiskLvm;
        c.Linux.AutoLogin = true;

        var yaml = AutoinstallGenerator.Generate(c);
        File.WriteAllText(Path.Combine(outDir, "user-data"), yaml);

        check(yaml.StartsWith("#cloud-config"), "ubuntu: começa com #cloud-config (exigido pelo cloud-init)");
        check(yaml.Contains("autoinstall:") && yaml.Contains("version: 1"), "ubuntu: bloco autoinstall versão 1");
        check(yaml.Contains("username: suporte"), "ubuntu: usuário criado");
        check(yaml.Contains("password: '$6$"), "ubuntu: senha gravada cifrada (SHA-512 crypt)");
        check(!yaml.Contains("S3nh@Forte!"), "ubuntu: senha em texto puro NÃO aparece no arquivo de resposta");
        check(yaml.Contains("locale: pt_BR.UTF-8") && yaml.Contains("layout: br") && yaml.Contains("variant: 'abnt2'"),
              "ubuntu: idioma e teclado ABNT2");
        check(yaml.Contains("timezone: America/Sao_Paulo"), "ubuntu: fuso horário");
        check(yaml.Contains("name: lvm") && yaml.Contains("size: largest"),
              "ubuntu: disco em LVM e maior disco fixo (nunca a mídia de boot)");
        check(yaml.Contains("install-server: true"), "ubuntu: servidor SSH quando marcado");
        check(yaml.Contains("shutdown: reboot"), "ubuntu: reinicia ao terminar");
        check(yaml.Contains("late-commands:") && yaml.Contains("curtin in-target") &&
              yaml.Contains("systemctl enable isoforge-firstboot.service"),
              "ubuntu: late-commands habilita o serviço de primeiro boot");
        check(yaml.Contains("/target/opt/isoforge"), "ubuntu: payload copiado para /opt/isoforge do sistema instalado");
        check(yaml.Contains("by-label/cidata"), "ubuntu: late-commands também procura o payload na ISO seed (rótulo cidata)");
        check(yaml.Contains("AutomaticLogin=") , "ubuntu: login automático configurado no GDM quando marcado");
        check(YamlLooksValid(yaml, out var yamlErro), "ubuntu: YAML estruturalmente válido — " + yamlErro);

        var meta = AutoinstallGenerator.MetaData(c);
        check(meta.Contains("instance-id:") && meta.Contains("local-hostname:"), "ubuntu: meta-data do NoCloud gerado");

        // Criptografia de disco.
        var enc = Cfg(TargetOs.Ubuntu);
        enc.Linux.DiskMode = LinuxDiskMode.EntireDiskEncrypted;
        enc.Linux.DiskPassword = "chave-luks";
        check(AutoinstallGenerator.Generate(enc).Contains("password: 'chave-luks'"),
              "ubuntu: senha do LUKS repassada ao instalador quando o disco é criptografado");

        // Disco fixado pelo usuário.
        var disco = Cfg(TargetOs.Ubuntu);
        disco.Linux.TargetDisk = "/dev/nvme0n1";
        var yamlDisco = AutoinstallGenerator.Generate(disco);
        check(yamlDisco.Contains("path: /dev/nvme0n1") && !yamlDisco.Contains("size: largest"),
              "ubuntu: disco fixado substitui a escolha automática");
    }

    // ------------------------------------------------------------------
    // Debian / Linux Mint — preseed
    // ------------------------------------------------------------------
    static void Debian(Action<bool, string> check, string outDir)
    {
        var c = Cfg(TargetOs.Debian, AppId.Firefox);
        c.Linux.DiskMode = LinuxDiskMode.EntireDiskLvm;
        c.AutoConnectWifi = true; c.WifiSsid = "MinhaRede"; c.WifiPassword = "segredo123";

        var preseed = PreseedGenerator.Generate(c);
        File.WriteAllText(Path.Combine(outDir, "preseed.cfg"), preseed);

        check(preseed.Contains("d-i debian-installer/locale string pt_BR.UTF-8"), "debian: locale");
        check(preseed.Contains("d-i keyboard-configuration/xkb-keymap select br"), "debian: teclado");
        check(preseed.Contains("d-i time/zone string America/Sao_Paulo"), "debian: fuso horário");
        check(preseed.Contains("d-i passwd/username string suporte"), "debian: usuário");
        check(preseed.Contains("d-i passwd/user-password-crypted password $6$"), "debian: senha cifrada");
        check(!preseed.Contains("S3nh@Forte!"), "debian: senha em texto puro NÃO aparece");
        check(preseed.Contains("sudo"), "debian: usuário administrador entra no grupo sudo");
        check(preseed.Contains("d-i partman-auto/method string lvm"), "debian: particionamento LVM");
        check(preseed.Contains("d-i partman/confirm_nooverwrite boolean true"), "debian: confirmações do particionador respondidas");
        check(preseed.Contains("netcfg/wireless_essid string MinhaRede"), "debian: Wi-Fi configurado já no instalador");
        check(preseed.Contains("popularity-contest/participate boolean false"), "debian: sem envio de estatísticas");

        var late = preseed.Replace("\r\n", "\n").Split('\n').First(l => l.StartsWith("d-i preseed/late_command"));
        check(late.Contains("in-target systemctl enable isoforge-firstboot.service") && late.Contains("; "),
              "debian: late_command em uma única linha habilitando o serviço de primeiro boot");
        check(late.Contains("/target/opt/isoforge"), "debian: late_command copia o payload para /opt/isoforge");

        var mint = PreseedGenerator.Generate(Cfg(TargetOs.LinuxMint));
        check(mint.Contains("ubiquity ubiquity/success_command string"),
              "mint: preseed inclui o success_command do Ubiquity (instalador do Mint)");

        var crypt = Cfg(TargetOs.Debian);
        crypt.Linux.DiskMode = LinuxDiskMode.EntireDiskEncrypted;
        crypt.Linux.DiskPassword = "luks-123";
        var cryptPreseed = PreseedGenerator.Generate(crypt);
        check(cryptPreseed.Contains("partman-auto/method string crypto") &&
              cryptPreseed.Contains("partman-crypto/passphrase password luks-123"),
              "debian: disco criptografado com a senha informada");

        var servidor = Cfg(TargetOs.Debian);
        servidor.Linux.Desktop = LinuxDesktop.None;
        check(PreseedGenerator.Generate(servidor).Contains("tasksel tasksel/first multiselect standard, ssh-server"),
              "debian: 'sem interface gráfica' instala apenas o conjunto padrão + ssh");
    }

    // ------------------------------------------------------------------
    // Fedora / Rocky / Alma — kickstart
    // ------------------------------------------------------------------
    static void RedHat(Action<bool, string> check, string outDir)
    {
        var c = Cfg(TargetOs.Fedora, AppId.Chrome);
        c.Linux.DiskMode = LinuxDiskMode.EntireDiskLvm;
        c.Linux.AutoLogin = true;

        var ks = KickstartGenerator.Generate(c);
        File.WriteAllText(Path.Combine(outDir, "ks.cfg"), ks);

        check(ks.Contains("lang pt_BR.UTF-8"), "kickstart: idioma");
        check(ks.Contains("keyboard --xlayouts='br (abnt2)'"), "kickstart: teclado ABNT2 no formato do Anaconda");
        check(ks.Contains("timezone America/Sao_Paulo --utc"), "kickstart: fuso horário");
        check(ks.Contains("user --name=suporte") && ks.Contains("--groups=wheel") && ks.Contains("--iscrypted"),
              "kickstart: usuário administrador com senha cifrada");
        check(!ks.Contains("S3nh@Forte!"), "kickstart: senha em texto puro NÃO aparece");
        check(ks.Contains("rootpw --lock"), "kickstart: root sem senha (administração por sudo)");
        check(ks.Contains("clearpart --all --initlabel") && ks.Contains("autopart --type=lvm"),
              "kickstart: disco limpo e particionado em LVM");
        check(ks.Contains("reboot") && ks.Contains("firstboot --disable"), "kickstart: reinicia e não abre o assistente inicial");

        var pacotes = ks.Split(new[] { "%packages" }, StringSplitOptions.None);
        check(pacotes.Length == 2 && pacotes[1].Contains("@^workstation-product-environment"),
              "kickstart: seção %packages com o ambiente gráfico do Fedora");
        check(CountOf(ks, "%end") == 3, "kickstart: as três seções (%packages e dois %post) são fechadas com %end");
        check(ks.Contains("%post --nochroot"), "kickstart: %post fora do chroot para ler a mídia de instalação");
        check(ks.Contains("/run/install/repo/isoforge"), "kickstart: payload copiado da mídia");
        check(ks.Contains("systemctl enable isoforge-firstboot.service"), "kickstart: serviço de primeiro boot habilitado");
        check(ks.Contains("AutomaticLogin=suporte"), "kickstart: login automático quando marcado");

        var rocky = Cfg(TargetOs.RockyLinux);
        rocky.Linux.Desktop = LinuxDesktop.None;
        check(KickstartGenerator.Generate(rocky).Contains("@^minimal-environment"),
              "kickstart: Rocky sem interface gráfica usa o ambiente mínimo");

        var alma = Cfg(TargetOs.AlmaLinux);
        alma.Linux.TargetDisk = "/dev/sdb";
        var ksAlma = KickstartGenerator.Generate(alma);
        check(ksAlma.Contains("ignoredisk --only-use=sdb") && ksAlma.Contains("clearpart --all --initlabel --drives=sdb"),
              "kickstart: disco fixado restringe o Anaconda a ele");

        var cripto = Cfg(TargetOs.Fedora);
        cripto.Linux.DiskMode = LinuxDiskMode.EntireDiskEncrypted;
        cripto.Linux.DiskPassword = "luks-abc";
        check(KickstartGenerator.Generate(cripto).Contains("autopart --type=lvm --encrypted --passphrase=luks-abc"),
              "kickstart: criptografia LUKS com a senha informada");
    }

    // ------------------------------------------------------------------
    // openSUSE — AutoYaST
    // ------------------------------------------------------------------
    static void Suse(Action<bool, string> check, string outDir)
    {
        var c = Cfg(TargetOs.OpenSuse, AppId.Firefox);
        var xml = AutoYastGenerator.Generate(c);
        File.WriteAllText(Path.Combine(outDir, "autoinst.xml"), xml);

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex) { check(false, "autoyast: XML bem-formado — " + ex.Message); return; }

        XNamespace ns = "http://www.suse.com/1.0/yast2ns";
        check(doc.Root!.Name == ns + "profile", "autoyast: raiz <profile> no namespace do YaST");
        check(doc.Descendants(ns + "timezone").Any(t => t.Value.Contains("America/Sao_Paulo")), "autoyast: fuso horário");
        check(doc.Descendants(ns + "keymap").Any(k => k.Value == "portuguese-br-abnt2"), "autoyast: teclado ABNT2");
        check(doc.Descendants(ns + "username").Any(u => u.Value == "suporte"), "autoyast: usuário criado");
        check(doc.Descendants(ns + "user_password").Any(p => p.Value.StartsWith("$6$")), "autoyast: senha cifrada");
        check(!xml.Contains("S3nh@Forte!"), "autoyast: senha em texto puro NÃO aparece");
        check(doc.Descendants(ns + "username").Any(u => u.Value == "root") &&
              doc.Descendants(ns + "user_password").Any(p => p.Value == "!"),
              "autoyast: root bloqueado quando a administração é por sudo");
        check(doc.Descendants(ns + "pattern").Any(p => p.Value == "gnome"), "autoyast: padrão de software do ambiente gráfico");
        check(xml.Contains("isoforge-firstboot"), "autoyast: serviço de primeiro boot habilitado");
        check(xml.Contains("<![CDATA[") && xml.Contains("/mnt/opt/isoforge"),
              "autoyast: script de chroot copia o payload para o sistema instalado");

        var criptoSuse = Cfg(TargetOs.OpenSuse);
        criptoSuse.Linux.DiskMode = LinuxDiskMode.EntireDiskEncrypted;
        criptoSuse.Linux.DiskPassword = "luks-suse";
        check(AutoYastGenerator.Generate(criptoSuse).Contains("luks-suse"), "autoyast: chave do LUKS aplicada");
    }

    // ------------------------------------------------------------------
    // Arch — archinstall
    // ------------------------------------------------------------------
    static void Arch(Action<bool, string> check, string outDir)
    {
        var c = Cfg(TargetOs.ArchLinux, AppId.AnyDesk);
        var json = ArchInstallGenerator.Generate(c);
        File.WriteAllText(Path.Combine(outDir, "user_configuration.json"), json);

        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (Exception ex) { check(false, "arch: JSON válido — " + ex.Message); return; }

        check(node!["hostname"]!.GetValue<string>() == "teste-pc01", "arch: hostname normalizado para minúsculas");
        check(node["timezone"]!.GetValue<string>() == "America/Sao_Paulo", "arch: fuso horário");
        check(node["locale_config"]!["kb_layout"]!.GetValue<string>() == "br-abnt2", "arch: teclado br-abnt2");
        check(node["profile_config"]!["profile"]!["details"]!.AsArray().Any(x => x!.GetValue<string>() == "GNOME"),
              "arch: perfil de desktop GNOME por padrão");
        check(node["custom-commands"]!.AsArray().Any(x => x!.GetValue<string>().Contains("isoforge-firstboot")),
              "arch: serviço de primeiro boot habilitado pelo archinstall");

        var creds = ArchInstallGenerator.Credentials(c);
        var credsNode = JsonNode.Parse(creds)!;
        check(credsNode["users"]!.AsArray()[0]!["enc_password"]!.GetValue<string>().StartsWith("$6$"),
              "arch: credenciais com senha cifrada");
        check(credsNode["users"]!.AsArray()[0]!["sudo"]!.GetValue<bool>(), "arch: usuário com sudo");
        check(!creds.Contains("S3nh@Forte!"), "arch: senha em texto puro NÃO aparece");

        var boot = ArchInstallGenerator.Bootstrap(c);
        File.WriteAllText(Path.Combine(outDir, "arch-autoinstall.sh"), boot);
        check(boot.StartsWith("#!/usr/bin/env bash"), "arch: script de bootstrap com shebang");
        check(boot.Contains("archinstall --config") && boot.Contains("--silent"), "arch: chama o archinstall em modo silencioso");
        check(boot.Contains("lsblk -dpno NAME,TYPE,RM,RO") && boot.Contains("$3==0"),
              "arch: escolhe o primeiro disco fixo, ignorando removíveis (nunca o pendrive)");
        check(boot.Contains("/run/archiso/bootmnt/isoforge"), "arch: acha o payload na mídia do archiso");
        check(boot.Contains("systemctl reboot"), "arch: reinicia ao terminar");
        check(IsAscii(boot), "arch: script de bootstrap é ASCII puro");

        var kde = Cfg(TargetOs.ArchLinux); kde.Linux.Desktop = LinuxDesktop.Kde;
        check(JsonNode.Parse(ArchInstallGenerator.Generate(kde))!["profile_config"]!["greeter"]!.GetValue<string>() == "sddm",
              "arch: KDE seleciona o gerenciador de login sddm");
    }

    // ------------------------------------------------------------------
    // Script de pós-instalação (equivalente do install.cmd)
    // ------------------------------------------------------------------
    static void PostInstall(Action<bool, string> check)
    {
        var c = Cfg(TargetOs.Ubuntu, AppId.OfficeOdt, AppId.Chrome, AppId.SevenZip, AppId.AnyDesk);
        c.AutoConnectWifi = true; c.WifiSsid = "MinhaRede"; c.WifiPassword = "segredo123";
        c.WindowsTheme = WindowsThemeMode.Dark;
        c.TaskbarAlign = TaskbarAlignment.Left;
        c.WallpaperPath = @"C:\imgs\wallpaper.jpg";
        c.VpnTunnels.Add(new VpnTunnel { Name = "VPN Matriz", RemoteGateway = "203.0.113.10", PresharedKey = "TestPsk123!" });
        c.Linux.RemoveSnap = true;
        c.Linux.DisableTelemetry = true;
        c.Linux.RemoveDefaultGames = true;
        c.Linux.DisableMotdAds = true;
        c.Linux.EnableFlatpak = true;
        c.Linux.ExtraPackages = "htop vim";
        c.Linux.InstallSshServer = true;

        var sh = LinuxPostInstall.Generate(c);

        check(sh.StartsWith("#!/usr/bin/env bash"), "pós-instalação: shebang na primeira linha");
        check(IsAscii(sh), "pós-instalação: script é ASCII puro (não depende do locale do instalador)");
        check(sh.Contains("/var/lib/isoforge/postinstall.done") && sh.Contains("já concluida".Replace("á", "a")),
              "pós-instalação: idempotente (marcador impede repetir em reboots)");
        check(sh.Contains("apt-get install -y") && sh.Contains("DEBIAN_FRONTEND=noninteractive"),
              "pós-instalação: usa apt no Ubuntu, sem perguntas");
        check(sh.Contains("wait_for_internet"), "pós-instalação: espera conexão antes de instalar (todo pacote precisa de rede)");
        check(sh.Contains("[1/4]") && sh.Contains("[4/4]"), "pós-instalação: mostra o progresso (X/N) como no Windows");
        check(sh.Contains("dl.google.com/linux/chrome/deb"), "pós-instalação: adiciona o repositório oficial do Chrome");
        check(sh.Contains("pkg_install_first '7zip' 'p7zip-full'"), "pós-instalação: 7-Zip usa a lista de alternativas");
        check(sh.Contains("nmcli connection up 'MinhaRede'") && sh.Contains("psk=segredo123"),
              "pós-instalação: perfil Wi-Fi do NetworkManager criado e conectado");
        check(sh.Contains("chmod 600 \"$WIFI_FILE\""), "pós-instalação: perfil de Wi-Fi com permissão restrita (contém a senha)");
        check(sh.Contains("color-scheme='prefer-dark'") && sh.Contains("dconf update"),
              "aparência: tema escuro aplicado a todos os usuários via dconf");
        check(sh.Contains("dock-position='LEFT'"), "aparência: dock à esquerda quando escolhido");
        check(sh.Contains("picture-uri='file:///usr/share/backgrounds/isoforge/wallpaper.jpg'"),
              "aparência: papel de parede apontado no dconf");
        check(sh.Contains("conn vpn-matriz") && sh.Contains("203.0.113.10 : PSK"),
              "VPN: túnel IPsec convertido em conexão strongSwan com a PSK");
        check(sh.Contains("chmod 600 /etc/ipsec.d/isoforge.secrets"), "VPN: arquivo de segredos com permissão restrita");
        check(sh.Contains("snap remove --purge") && sh.Contains("Pin-Priority: -10"),
              "otimização: snapd removido e bloqueado no apt");
        check(sh.Contains("popularity-contest") && sh.Contains("whoopsie"), "otimização: telemetria desativada");
        check(sh.Contains("motd-news"), "otimização: propaganda do MOTD desativada");
        check(sh.Contains("gnome-mines"), "otimização: jogos de fábrica removidos");
        check(sh.Contains("pkg_install 'htop' 'vim'"), "pacotes extras instalados");
        check(sh.Contains("openssh-server"), "servidor SSH instalado quando marcado");
        check(sh.Contains("isoforge-report.sh"), "relatório chamado ao final");
        check(sh.Contains("systemctl disable isoforge-firstboot.service"),
              "pós-instalação: o serviço se desabilita depois de concluir");

        // Ordem importa: em shell a função precisa existir antes da primeira chamada.
        var arch = Cfg(TargetOs.ArchLinux, AppId.AnyDesk);   // no Arch o AnyDesk vem por Flatpak
        var shArch = LinuxPostInstall.Generate(arch).Replace("\r\n", "\n");
        var definicao = shArch.IndexOf("ensure_flatpak() {", StringComparison.Ordinal);
        var chamada = shArch.IndexOf("\nensure_flatpak\n", StringComparison.Ordinal);
        check(definicao >= 0 && chamada > definicao,
              "pós-instalação: ensure_flatpak é definido antes de ser chamado");
        check(shArch.Contains("pacman -S --noconfirm --needed"), "pós-instalação: usa pacman no Arch");
        check(shArch.Contains("flatpak install -y --noninteractive flathub 'com.anydesk.Anydesk'"),
              "pós-instalação: AnyDesk instalado pelo Flathub no Arch");

        check(LinuxPostInstall.Generate(Cfg(TargetOs.Fedora)).Contains("dnf install -y"), "pós-instalação: usa dnf no Fedora");
        check(LinuxPostInstall.Generate(Cfg(TargetOs.RockyLinux)).Contains("epel-release"),
              "pós-instalação: EPEL habilitado no Rocky/Alma (onde vive o p7zip)");
        check(LinuxPostInstall.Generate(Cfg(TargetOs.OpenSuse)).Contains("zypper --non-interactive install"),
              "pós-instalação: usa zypper no openSUSE");

        var vazio = LinuxPostInstall.Generate(Cfg(TargetOs.Ubuntu));
        check(!vazio.Contains("[1/"), "pós-instalação: sem apps selecionados, não gera a seção de aplicativos");
        check(IsAscii(vazio), "pós-instalação: script mínimo também é ASCII puro");

        // Serviço systemd
        var svc = LinuxPostInstall.FirstBootService();
        check(svc.Contains("[Unit]") && svc.Contains("[Service]") && svc.Contains("[Install]"),
              "systemd: unidade com as três seções");
        check(svc.Contains("Type=oneshot") && svc.Contains("WantedBy=multi-user.target"),
              "systemd: executa uma vez e é habilitada no multi-user");
        check(svc.Contains("ConditionPathExists=!/var/lib/isoforge/postinstall.done"),
              "systemd: não roda de novo depois de concluída");
        check(svc.Contains("After=network-online.target"), "systemd: espera a rede subir");

        // Relatório
        var rep = LinuxReportGenerator.Generate(c);
        check(rep.Contains("IsoForge-Provisionamento.html") && rep.Contains("<!doctype html>"),
              "relatório: gera o HTML de provisionamento");
        check(rep.Contains("LibreOffice") && rep.Contains("Google Chrome"), "relatório: lista os programas instalados");
        check(rep.Contains("xdg-user-dir DESKTOP"), "relatório: copiado para a área de trabalho (respeitando o idioma da pasta)");
        check(IsAscii(rep), "relatório: script é ASCII puro");
    }

    // ------------------------------------------------------------------
    // Seleção de unidade (nome da máquina = prefixo + nº de série)
    // ------------------------------------------------------------------
    static void UnitSelection(Action<bool, string> check)
    {
        var c = Cfg(TargetOs.Ubuntu);
        c.UseUnitSelection = true;
        var sh = LinuxPostInstall.Generate(c);

        check(sh.Contains("/etc/isoforge/units.conf") && sh.Contains("Matriz|MTZ"),
              "unidade: lista de unidades gravada no sistema instalado");
        check(sh.Contains("/usr/local/sbin/isoforge-set-hostname") && sh.Contains("/usr/local/bin/isoforge-select-unit"),
              "unidade: scripts de seleção e renomeação instalados");
        check(sh.Contains("/etc/sudoers.d/isoforge-unit") && sh.Contains("NOPASSWD: /usr/local/sbin/isoforge-set-hostname"),
              "unidade: sudo sem senha limitado a UM comando específico");
        check(sh.Contains("/etc/xdg/autostart/isoforge-select-unit.desktop"),
              "unidade: tela aparece no primeiro login gráfico");
        check(sh.Contains("/etc/profile.d/isoforge-select-unit.sh"),
              "unidade: em servidor (sem ambiente gráfico) a escolha é feita no console");

        var setHost = LinuxPostInstall.SetHostnameScript();
        check(setHost.Contains("grep -Eq '^[A-Za-z0-9-]{1,20}$'"),
              "unidade: prefixo é validado antes de virar hostname (não aceita injeção)");
        check(setHost.Contains("dmidecode -s system-serial-number") && setHost.Contains("product_serial"),
              "unidade: número de série lido do BIOS, com alternativa via sysfs");
        check(setHost.Contains("hostnamectl set-hostname") && setHost.Contains("127.0.1.1"),
              "unidade: hostname aplicado e /etc/hosts atualizado");
        check(setHost.Contains("cut -c1-63"), "unidade: hostname limitado a 63 caracteres");
        check(setHost.Contains("rm -f /etc/xdg/autostart/isoforge-select-unit.desktop"),
              "unidade: a tela não volta a aparecer depois de escolhida");
        check(IsAscii(setHost), "unidade: script de hostname é ASCII puro");

        var select = LinuxPostInstall.SelectUnitScript(c);
        check(select.Contains("zenity --list") && select.Contains("read -r escolha"),
              "unidade: tela gráfica (zenity) com alternativa em texto");
        check(select.Contains("systemctl reboot"), "unidade: reinicia para aplicar o novo nome");
        check(IsAscii(select), "unidade: script de seleção é ASCII puro");

        var teste = Cfg(TargetOs.Ubuntu);
        teste.UseUnitSelection = true; teste.SandboxTest = true;
        check(!LinuxPostInstall.SelectUnitScript(teste).Contains("systemctl reboot"),
              "unidade: no modo de teste o reinício é pulado");
    }

    // ------------------------------------------------------------------
    // Conjunto de arquivos e parâmetros de boot
    // ------------------------------------------------------------------
    static void AnswerFiles(Action<bool, string> check)
    {
        bool Has(BuildConfig c, string relativePath) =>
            LinuxAnswerFile.Generate(c).Any(f => f.RelativePath == relativePath);

        check(Has(Cfg(TargetOs.Ubuntu), "isoforge/user-data") && Has(Cfg(TargetOs.Ubuntu), "isoforge/meta-data"),
              "arquivos: Ubuntu gera user-data + meta-data");
        check(Has(Cfg(TargetOs.Debian), "isoforge/preseed.cfg"), "arquivos: Debian gera preseed.cfg");
        check(Has(Cfg(TargetOs.Fedora), "isoforge/ks.cfg"), "arquivos: Fedora gera ks.cfg");
        check(Has(Cfg(TargetOs.OpenSuse), "isoforge/autoinst.xml"), "arquivos: openSUSE gera autoinst.xml");
        check(Has(Cfg(TargetOs.ArchLinux), "isoforge/user_configuration.json") &&
              Has(Cfg(TargetOs.ArchLinux), "isoforge/user_credentials.json") &&
              Has(Cfg(TargetOs.ArchLinux), "isoforge/arch-autoinstall.sh"),
              "arquivos: Arch gera perfil + credenciais + script de bootstrap");

        foreach (var os in OsCatalog.All.Where(o => o.IsLinux))
        {
            var files = LinuxAnswerFile.Generate(Cfg(os.Id));
            check(files.Any(f => f.RelativePath == "isoforge/isoforge-postinstall.sh"),
                  $"arquivos: {os.Name} inclui a pós-instalação");
            check(files.Any(f => f.RelativePath == "isoforge/isoforge-firstboot.service"),
                  $"arquivos: {os.Name} inclui o serviço systemd");
            check(files.Any(f => f.RelativePath.EndsWith(os.AnswerFileName)),
                  $"arquivos: {os.Name} inclui o arquivo de resposta {os.AnswerFileName}");
        }

        check(LinuxAnswerFile.Generate(Cfg(TargetOs.Ubuntu)).Any(f => f.RelativePath == "isoforge/LEIA-ME.txt"),
              "arquivos: instruções de uso incluídas");
        var semRelatorio = Cfg(TargetOs.Ubuntu); semRelatorio.GenerateReport = false;
        check(!LinuxAnswerFile.Generate(semRelatorio).Any(f => f.RelativePath.Contains("report")),
              "arquivos: relatório desligado não gera o script");

        // Parâmetros de boot
        var grub = LinuxAnswerFile.KernelParams(Cfg(TargetOs.Ubuntu), "UBUNTU", forGrub: true);
        var isolinux = LinuxAnswerFile.KernelParams(Cfg(TargetOs.Ubuntu), "UBUNTU", forGrub: false);
        check(grub.Contains("autoinstall") && grub.Contains("ds=nocloud\\;s=/cdrom/isoforge/"),
              "boot: Ubuntu no GRUB escapa o ';' (exigência da sintaxe do grub.cfg)");
        check(isolinux.Contains("ds=nocloud;s=/cdrom/isoforge/") && !isolinux.Contains("\\;"),
              "boot: Ubuntu no isolinux usa ';' sem escape");
        var debianParams = LinuxAnswerFile.KernelParams(Cfg(TargetOs.Debian), "DEBIAN", false);
        check(debianParams.Contains("preseed/file=/cdrom/isoforge/preseed.cfg"), "boot: Debian aponta para o preseed da mídia");
        foreach (var os in OsCatalog.All.Where(o => o.IsLinux))
            check(!LinuxAnswerFile.KernelParams(Cfg(os.Id), "L", false).Contains("---"),
                  $"boot: {os.Name} não injeta um segundo separador '---' (o da linha original já existe)");
        check(LinuxAnswerFile.KernelParams(Cfg(TargetOs.LinuxMint), "MINT", false).Contains("automatic-ubiquity"),
              "boot: Mint usa o modo automático do Ubiquity");
        check(LinuxAnswerFile.KernelParams(Cfg(TargetOs.Fedora), "Fedora 40", false)
                  .Contains("inst.ks=hd:LABEL=Fedora\\x2040:/isoforge/ks.cfg"),
              "boot: Fedora localiza o ks.cfg pelo rótulo, com o espaço escapado");
        check(LinuxAnswerFile.KernelParams(Cfg(TargetOs.OpenSuse), "SUSE", false).Contains("autoyast=cd:/isoforge/autoinst.xml"),
              "boot: openSUSE aponta para o AutoYaST na mídia");
        check(LinuxAnswerFile.KernelParams(Cfg(TargetOs.ArchLinux), "ARCH", false).Contains("script=/isoforge/arch-autoinstall.sh"),
              "boot: Arch usa o parâmetro script= do archiso");

        // Modo de entrega
        check(LinuxAnswerFile.SeedSupported(TargetOs.Ubuntu) && LinuxAnswerFile.SeedSupported(TargetOs.Fedora),
              "entrega: ISO seed suportada onde o instalador procura o volume sozinho");
        check(!LinuxAnswerFile.SeedSupported(TargetOs.Debian) && !LinuxAnswerFile.SeedSupported(TargetOs.ArchLinux),
              "entrega: ISO seed não é oferecida onde o instalador não a encontraria");
        check(LinuxAnswerFile.SeedLabel(TargetOs.Ubuntu) == "cidata" && LinuxAnswerFile.SeedLabel(TargetOs.Fedora) == "OEMDRV",
              "entrega: rótulos exigidos por cada instalador (cidata / OEMDRV)");

        var seedDebian = Cfg(TargetOs.Debian); seedDebian.Linux.Delivery = LinuxDeliveryMode.SeedIso;
        check(LinuxAnswerFile.EffectiveDelivery(seedDebian) == LinuxDeliveryMode.Repack,
              "entrega: pedido de seed no Debian cai para reempacotamento (o instalador não acharia o arquivo)");

        var seedUbuntu = Cfg(TargetOs.Ubuntu); seedUbuntu.Linux.Delivery = LinuxDeliveryMode.SeedIso;
        var seedFiles = LinuxAnswerFile.Generate(seedUbuntu);
        check(seedFiles.Any(f => f.RelativePath == "user-data") && seedFiles.Any(f => f.RelativePath == "meta-data"),
              "entrega: no modo seed o cloud-init encontra user-data/meta-data na raiz do volume");
        var seedFedora = Cfg(TargetOs.Fedora); seedFedora.Linux.Delivery = LinuxDeliveryMode.SeedIso;
        check(LinuxAnswerFile.Generate(seedFedora).Any(f => f.RelativePath == "ks.cfg"),
              "entrega: no modo seed o Anaconda encontra /ks.cfg na raiz do volume OEMDRV");
    }

    // ------------------------------------------------------------------
    // Ajuste dos bootloaders
    // ------------------------------------------------------------------
    static void Bootloader(Action<bool, string> check)
    {
        const string parametros = "autoinstall ds=nocloud;s=/cdrom/isoforge/";

        var grub = "set timeout=30\nmenuentry \"Install Ubuntu\" {\n\tlinux\t/casper/vmlinuz  quiet splash ---\n\tinitrd\t/casper/initrd\n}\n";
        var grubPatched = BootloaderPatcher.PatchGrub(grub, parametros, out var nGrub);
        check(nGrub == 1, "bootloader: entrada do GRUB reconhecida");
        check(grubPatched.Contains($"quiet splash {parametros} ---"),
              "bootloader: parâmetros inseridos ANTES do separador '---'");
        check(grubPatched.Contains("set timeout=5"), "bootloader: tempo do menu do GRUB reduzido");

        var novamente = BootloaderPatcher.PatchGrub(grubPatched, parametros, out var nGrub2);
        check(nGrub2 == 0 && novamente.Split("autoinstall").Length == 2,
              "bootloader: reaplicar não duplica os parâmetros");

        var isolinux = "prompt 1\ntimeout 300\nlabel install\n  kernel /install.amd/vmlinuz\n  append vga=788 initrd=/install.amd/initrd.gz --- quiet\n";
        var isoPatched = BootloaderPatcher.PatchIsolinux(isolinux, parametros, out var nIso);
        check(nIso == 1 && isoPatched.Contains($"initrd.gz {parametros} --- quiet"),
              "bootloader: entrada do isolinux recebe os parâmetros antes do '---'");
        check(isoPatched.Contains("timeout 50") && isoPatched.Contains("prompt 0"),
              "bootloader: menu do isolinux não fica esperando");

        var systemd = "title   Arch Linux install medium\nlinux   /arch/boot/x86_64/vmlinuz-linux\noptions archisobasedir=arch archisolabel=ARCH_202406\n";
        var sdPatched = BootloaderPatcher.PatchSystemdBoot(systemd, parametros, out var nSd);
        check(nSd == 1 && sdPatched.Contains("archisolabel=ARCH_202406 " + parametros),
              "bootloader: entrada do systemd-boot (usada pelo Arch) recebe os parâmetros");

        // Sem separador '---', os parâmetros vão para o fim da linha.
        var semSep = BootloaderPatcher.PatchGrub("menuentry x {\n  linux /vmlinuz quiet\n}\n", parametros, out _);
        check(semSep.Contains("linux /vmlinuz quiet " + parametros), "bootloader: sem '---', anexa no fim");

        check(BootloaderPatcher.KindOf(Path.Combine("x", "boot", "grub", "grub.cfg")) == BootloaderPatcher.ConfigKind.Grub,
              "bootloader: grub.cfg identificado");
        check(BootloaderPatcher.KindOf(Path.Combine("x", "loader", "entries", "arch.conf")) == BootloaderPatcher.ConfigKind.SystemdBoot,
              "bootloader: entrada do systemd-boot identificada");
        check(BootloaderPatcher.KindOf(Path.Combine("x", "isolinux", "txt.cfg")) == BootloaderPatcher.ConfigKind.Isolinux,
              "bootloader: configuração do isolinux identificada");
    }

    // ------------------------------------------------------------------
    // Extração das imagens de boot (El Torito)
    // ------------------------------------------------------------------
    static void ElToritoTests(Action<bool, string> check, string outDir)
    {
        var dir = Path.Combine(outDir, "eltorito");
        Directory.CreateDirectory(dir);
        var iso = Path.Combine(dir, "bootavel.iso");
        File.WriteAllBytes(iso, FakeIso.BuildBootable("Ubuntu 24.04 LTS amd64"));

        var imagens = ElTorito.Read(iso);
        check(imagens.Count == 2, $"el torito: as duas imagens de boot foram lidas (encontradas: {imagens.Count})");
        var bios = imagens.FirstOrDefault(i => !i.IsEfi);
        var efi = imagens.FirstOrDefault(i => i.IsEfi);
        check(bios != null && bios.Length == 4 * 512, "el torito: imagem BIOS com o tamanho declarado no catálogo");
        check(efi != null && efi.Length == 8 * 512,
              "el torito: tamanho real da imagem UEFI vem do BPB do FAT (o catálogo subdimensiona)");

        var (biosPath, efiPath) = ElTorito.Extract(iso, Path.Combine(dir, "saida"));
        check(biosPath != null && new FileInfo(biosPath).Length == 2048, "el torito: imagem BIOS extraída para disco");
        check(efiPath != null && new FileInfo(efiPath).Length == 4096, "el torito: imagem UEFI extraída para disco");

        // ISO sem El Torito: não deve lançar nem inventar imagens.
        var semBoot = Path.Combine(dir, "sem-boot.iso");
        File.WriteAllBytes(semBoot, FakeIso.Build("DADOS"));
        check(ElTorito.Read(semBoot).Count == 0, "el torito: ISO não bootável devolve lista vazia");
        check(ElTorito.Extract(semBoot, Path.Combine(dir, "vazio")) == (null, null),
              "el torito: extração de ISO não bootável não produz arquivos");
    }

    // ------------------------------------------------------------------
    // Validação da configuração
    // ------------------------------------------------------------------
    static void Validation(Action<bool, string> check)
    {
        var pipeline = new LinuxIsoPipeline(new Progress<string>(_ => { }));

        string? Erro(Action<BuildConfig> ajuste)
        {
            var c = Cfg(TargetOs.Ubuntu);
            ajuste(c);
            try { pipeline.Validate(c, dryRun: true); return null; }
            catch (Exception ex) { return ex.Message; }
        }

        check(Erro(_ => { }) == null, "validação: configuração típica é aceita");
        check(Erro(c => c.UserName = "root")?.Contains("root") == true, "validação: recusa o usuário 'root'");
        check(Erro(c => c.UserName = "Suporte TI") != null, "validação: recusa nome de usuário inválido para Linux");
        check(Erro(c => c.UserName = "") != null, "validação: exige o nome do usuário");
        check(Erro(c => c.Password = "") != null, "validação: exige a senha");
        check(Erro(c => { c.Linux.DiskMode = LinuxDiskMode.EntireDiskEncrypted; c.Linux.DiskPassword = ""; }) != null,
              "validação: criptografia de disco exige a senha do LUKS");
        check(Erro(c => c.Linux.TargetDisk = "sda")?.Contains("/dev/") == true,
              "validação: disco precisa ser um caminho de dispositivo");
        check(Erro(c => c.UserName = "ti-suporte") == null, "validação: aceita nomes com hífen");
        check(Erro(c => { c.Linux.DiskMode = LinuxDiskMode.EntireDiskEncrypted; c.Linux.DiskPassword = "x"; }) == null,
              "validação: criptografia com senha é aceita");

        check(LinuxIsoPipeline.SanitizeLabel("Ubuntu 24.04.1 LTS amd64", Cfg(TargetOs.Ubuntu)) == "Ubuntu_24.04.1_LTS_amd64",
              "rótulo: espaços viram '_' para o oscdimg");
        check(LinuxIsoPipeline.SanitizeLabel(new string('X', 60), Cfg(TargetOs.Ubuntu)).Length == 32,
              "rótulo: limitado a 32 caracteres");
        check(LinuxIsoPipeline.SanitizeLabel("", Cfg(TargetOs.Fedora)) == "FEDORA", "rótulo: vazio usa o nome da distro");
    }

    // ------------------------------------------------------------------
    // Payload gravado em disco (dry-run)
    // ------------------------------------------------------------------
    static void Payload(Action<bool, string> check, string outDir)
    {
        var dir = Path.Combine(outDir, "linux-dryrun");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);

        var c = Cfg(TargetOs.Ubuntu, AppId.Chrome, AppId.SevenZip);
        c.UseUnitSelection = true;

        // Papel de parede e script personalizado, para conferir a cópia.
        Directory.CreateDirectory(Path.Combine(outDir, "src"));
        var wallpaper = Path.Combine(outDir, "src", "wallpaper.jpg");
        File.WriteAllBytes(wallpaper, new byte[64]);
        var post = Path.Combine(outDir, "src", "pos.sh");
        File.WriteAllText(post, "#!/bin/sh\r\necho ola\r\n");
        c.WallpaperPath = wallpaper;
        c.PostScriptPath = post;

        new LinuxIsoPipeline(new Progress<string>(_ => { })).DryRun(c, dir);

        var payload = Path.Combine(dir, "isoforge");
        check(File.Exists(Path.Combine(payload, "user-data")), "dry-run linux: user-data gravado");
        check(File.Exists(Path.Combine(payload, "isoforge-postinstall.sh")), "dry-run linux: pós-instalação gravada");
        check(File.Exists(Path.Combine(payload, "isoforge-firstboot.service")), "dry-run linux: serviço systemd gravado");
        check(File.Exists(Path.Combine(payload, "wallpaper.jpg")), "dry-run linux: papel de parede copiado");
        check(File.Exists(Path.Combine(payload, "pos.sh")), "dry-run linux: script personalizado copiado");
        check(File.Exists(Path.Combine(dir, "parametros-de-boot.txt")), "dry-run linux: parâmetros de boot documentados");

        var bytes = File.ReadAllBytes(Path.Combine(payload, "isoforge-postinstall.sh"));
        check(bytes.Length > 3 && !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF),
              "dry-run linux: script gravado SEM BOM (um BOM quebraria o shebang)");
        check(!Encoding.UTF8.GetString(bytes).Contains('\r'),
              "dry-run linux: script gravado com fim de linha LF (CRLF quebraria a execução)");
        check(!File.ReadAllText(Path.Combine(payload, "pos.sh")).Contains('\r'),
              "dry-run linux: script personalizado convertido para LF");
        check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(payload, "user-data"))).Contains('\r'),
              "dry-run linux: arquivo de resposta também em LF");
    }

    // ------------------------------------------------------------------
    // Utilidades
    // ------------------------------------------------------------------
    static bool IsAscii(string text) => !text.Any(c => c > 126);

    static int CountOf(string text, string needle)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>
    /// Sanidade estrutural do YAML gerado: sem tabulação, indentação par e chaves com
    /// espaço depois dos dois-pontos. Pega os erros clássicos que quebram o cloud-init.
    /// </summary>
    static bool YamlLooksValid(string yaml, out string erro)
    {
        erro = "ok";
        var linhas = yaml.Replace("\r\n", "\n").Split('\n');
        bool emBloco = false;
        int recuoBloco = 0;

        for (int i = 0; i < linhas.Length; i++)
        {
            var linha = linhas[i];
            if (linha.Length == 0) continue;

            if (linha.Contains('\t')) { erro = $"tabulação na linha {i + 1}"; return false; }

            int recuo = linha.Length - linha.TrimStart(' ').Length;
            var conteudo = linha.TrimStart(' ');
            if (conteudo.StartsWith('#')) continue;

            // Blocos literais (| e |-) têm conteúdo livre.
            if (emBloco)
            {
                if (recuo > recuoBloco) continue;
                emBloco = false;
            }
            if (conteudo.EndsWith("|") || conteudo.EndsWith("|-"))
            {
                emBloco = true; recuoBloco = recuo; continue;
            }

            if (recuo % 2 != 0) { erro = $"indentação ímpar na linha {i + 1}: {linha}"; return false; }
            if (conteudo.StartsWith("- ")) continue;

            var dois = conteudo.IndexOf(':');
            if (dois < 0) { erro = $"linha {i + 1} não é chave nem item de lista: {linha}"; return false; }
            var depois = conteudo[(dois + 1)..];
            if (depois.Length > 0 && depois[0] != ' ')
            {
                erro = $"falta espaço depois de ':' na linha {i + 1}: {linha}";
                return false;
            }
        }
        return true;
    }
}
