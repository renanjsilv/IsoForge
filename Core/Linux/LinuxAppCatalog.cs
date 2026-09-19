using IsoForge.Models;

namespace IsoForge.Core.Linux;

/// <summary>
/// Receita de instalação de um aplicativo em uma família Linux.
/// </summary>
/// <param name="DisplayName">Nome do programa como ele se chama no Linux (ex.: LibreOffice).</param>
/// <param name="Packages">Pacotes instalados (todos).</param>
/// <param name="FirstAvailable">
/// Alternativas do mesmo programa: instala a PRIMEIRA que existir nos repositórios da distro
/// (ex.: "7zip" no Ubuntu 24.04, "p7zip-full" nas versões anteriores).
/// </param>
/// <param name="RepoSetup">Shell que adiciona o repositório oficial do fabricante (roda antes).</param>
/// <param name="PostInstall">Shell executado depois da instalação.</param>
/// <param name="FlatpakId">App do Flathub usado quando não há pacote nativo na distro.</param>
/// <param name="Note">Observação mostrada na interface e gravada no log.</param>
public record LinuxAppRecipe(
    string DisplayName,
    string[] Packages,
    string[] FirstAvailable,
    string RepoSetup = "",
    string PostInstall = "",
    string FlatpakId = "",
    string Note = "")
{
    /// <summary>Existe alguma forma de instalar este app nesta distro?</summary>
    public bool Installable => Packages.Length > 0 || FirstAvailable.Length > 0 || FlatpakId.Length > 0;
}

/// <summary>
/// Traduz os aplicativos do catálogo do IsoForge (os mesmos cards do Windows) para os
/// pacotes/repositórios equivalentes de cada família Linux. Onde o programa não existe para
/// Linux (Adobe Reader, Notepad++, Visual C++), instala o equivalente consagrado e registra
/// a substituição no log e na interface.
/// </summary>
public static class LinuxAppCatalog
{
    /// <summary>Aplicativos do catálogo disponíveis no Linux, na ordem dos cards.</summary>
    public static readonly AppId[] Supported =
    {
        AppId.OfficeOdt, AppId.AnyDesk, AppId.SevenZip, AppId.FortiClient,
        AppId.AdobeReader, AppId.Chrome, AppId.Firefox, AppId.NotepadPlus, AppId.VcRedist
    };

    /// <summary>O FortiClient "mais recente" e o 7.4.1 são o mesmo pacote no Linux.</summary>
    static AppId Normalize(AppId id) => id == AppId.FortiClientLatest ? AppId.FortiClient : id;

    /// <summary>Nome do app no Windows (usado para explicar a substituição).</summary>
    public static string WindowsName(AppId id) => Normalize(id) switch
    {
        AppId.OfficeOdt => "Office 365",
        AppId.AnyDesk => "AnyDesk",
        AppId.SevenZip => "7-Zip",
        AppId.FortiClient => "FortiClient",
        AppId.AdobeReader => "Adobe Acrobat Reader",
        AppId.Chrome => "Google Chrome",
        AppId.Firefox => "Mozilla Firefox",
        AppId.NotepadPlus => "Notepad++",
        AppId.VcRedist => "Visual C++ 2015-2022",
        _ => id.ToString()
    };

    /// <summary>Receita para o app na distro escolhida. Null quando não há equivalente.</summary>
    public static LinuxAppRecipe? Recipe(AppId id, BuildConfig cfg)
    {
        var family = OsCatalog.FamilyOf(cfg.Os);
        return Normalize(id) switch
        {
            AppId.OfficeOdt => LibreOffice(family, cfg.OfficeLanguage),
            AppId.AnyDesk => AnyDesk(family),
            AppId.SevenZip => SevenZip(family),
            AppId.FortiClient => FortiClient(family, cfg.Os),
            AppId.AdobeReader => PdfReader(family, cfg.Linux.Desktop),
            AppId.Chrome => Chrome(family),
            AppId.Firefox => Firefox(family),
            AppId.NotepadPlus => TextEditor(family),
            AppId.VcRedist => BuildTools(family),
            _ => null
        };
    }

    /// <summary>Nome exibido no card quando o Linux está selecionado.</summary>
    public static string DisplayName(AppId id, BuildConfig cfg) => Recipe(id, cfg)?.DisplayName ?? WindowsName(id);

    // ------------------------------------------------------------------
    // Office 365 -> LibreOffice (mesma finalidade, abre .docx/.xlsx/.pptx)
    // ------------------------------------------------------------------
    static LinuxAppRecipe LibreOffice(OsFamily family, string officeLanguage)
    {
        var (core, lang) = family switch
        {
            OsFamily.Debian => ("libreoffice", officeLanguage switch
            {
                "pt-br" => "libreoffice-l10n-pt-br",
                "es-es" => "libreoffice-l10n-es",
                "pt-pt" => "libreoffice-l10n-pt",
                _ => ""
            }),
            OsFamily.RedHat => ("libreoffice", officeLanguage switch
            {
                "pt-br" => "libreoffice-langpack-pt_BR",
                "es-es" => "libreoffice-langpack-es",
                "pt-pt" => "libreoffice-langpack-pt_PT",
                _ => ""
            }),
            OsFamily.Suse => ("libreoffice", officeLanguage switch
            {
                "pt-br" => "libreoffice-l10n-pt_BR",
                "es-es" => "libreoffice-l10n-es",
                "pt-pt" => "libreoffice-l10n-pt_PT",
                _ => ""
            }),
            _ => ("libreoffice-fresh", officeLanguage switch
            {
                "pt-br" => "libreoffice-fresh-pt-br",
                "es-es" => "libreoffice-fresh-es",
                "pt-pt" => "libreoffice-fresh-pt",
                _ => ""
            })
        };

        var pkgs = string.IsNullOrEmpty(lang) ? new[] { core } : new[] { core, lang };
        return new LinuxAppRecipe(
            "LibreOffice",
            pkgs,
            Array.Empty<string>(),
            Note: "O Microsoft Office não roda nativamente no Linux. O IsoForge instala o LibreOffice " +
                  "(abre e salva .docx/.xlsx/.pptx) com o pacote de idioma escolhido.");
    }

    // ------------------------------------------------------------------
    // AnyDesk — repositório oficial da AnyDesk (deb/rpm) e Flathub no Arch
    // ------------------------------------------------------------------
    static LinuxAppRecipe AnyDesk(OsFamily family) => family switch
    {
        OsFamily.Debian => new LinuxAppRecipe("AnyDesk", new[] { "anydesk" }, Array.Empty<string>(),
            RepoSetup: """
            install -m 0755 -d /etc/apt/keyrings
            curl -fsSL https://keys.anydesk.com/repos/DEB-GPG-KEY | gpg --dearmor -o /etc/apt/keyrings/anydesk.gpg
            chmod 0644 /etc/apt/keyrings/anydesk.gpg
            echo "deb [signed-by=/etc/apt/keyrings/anydesk.gpg] http://deb.anydesk.com/ all main" > /etc/apt/sources.list.d/anydesk.list
            apt-get update -y
            """,
            PostInstall: "systemctl enable --now anydesk.service 2>/dev/null || true"),

        OsFamily.RedHat => new LinuxAppRecipe("AnyDesk", new[] { "anydesk" }, Array.Empty<string>(),
            RepoSetup: """
            cat > /etc/yum.repos.d/AnyDesk.repo <<'ISOFORGE_EOF'
            [anydesk]
            name=AnyDesk RHEL - stable
            baseurl=http://rpm.anydesk.com/rhel/$basearch/
            gpgcheck=1
            repo_gpgcheck=1
            gpgkey=https://keys.anydesk.com/repos/RPM-GPG-KEY
            ISOFORGE_EOF
            """,
            PostInstall: "systemctl enable --now anydesk.service 2>/dev/null || true"),

        OsFamily.Suse => new LinuxAppRecipe("AnyDesk", new[] { "anydesk" }, Array.Empty<string>(),
            RepoSetup: """
            rpm --import https://keys.anydesk.com/repos/RPM-GPG-KEY || true
            zypper --non-interactive addrepo --refresh http://rpm.anydesk.com/opensuse/x86_64/ anydesk || true
            zypper --non-interactive --gpg-auto-import-keys refresh || true
            """,
            PostInstall: "systemctl enable --now anydesk.service 2>/dev/null || true"),

        // No Arch o AnyDesk fica no AUR (não instalável sem helper): usa o pacote oficial do Flathub.
        _ => new LinuxAppRecipe("AnyDesk", Array.Empty<string>(), Array.Empty<string>(),
            FlatpakId: "com.anydesk.Anydesk",
            Note: "No Arch o AnyDesk só existe no AUR; o IsoForge instala a versão oficial via Flatpak (Flathub).")
    };

    // ------------------------------------------------------------------
    // 7-Zip -> 7zip / p7zip (mesmo projeto, empacotado com nomes diferentes)
    // ------------------------------------------------------------------
    static LinuxAppRecipe SevenZip(OsFamily family) => family switch
    {
        // "7zip" existe no Ubuntu 24.04+/Debian 13; "p7zip-full" nas versões anteriores.
        OsFamily.Debian => new LinuxAppRecipe("7-Zip (p7zip)", Array.Empty<string>(),
            new[] { "7zip", "p7zip-full" }),
        OsFamily.RedHat => new LinuxAppRecipe("7-Zip (p7zip)", Array.Empty<string>(),
            new[] { "7zip", "p7zip" },
            Note: "Em Rocky/AlmaLinux o pacote vem do EPEL, habilitado automaticamente."),
        OsFamily.Suse => new LinuxAppRecipe("7-Zip (p7zip)", Array.Empty<string>(), new[] { "7zip", "p7zip" }),
        _ => new LinuxAppRecipe("7-Zip (p7zip)", Array.Empty<string>(), new[] { "7zip", "p7zip" })
    };

    // ------------------------------------------------------------------
    // FortiClient — repositório oficial da Fortinet (deb/rpm); openfortivpn onde não há pacote
    // ------------------------------------------------------------------
    static LinuxAppRecipe FortiClient(OsFamily family, TargetOs os)
    {
        const string note = "O FortiClient para Linux é somente SSL-VPN (não faz IPsec). " +
                            "Os túneis IPsec configurados na aba Aplicativos são convertidos para strongSwan.";

        return family switch
        {
            OsFamily.Debian => new LinuxAppRecipe("FortiClient VPN", new[] { "forticlient" }, Array.Empty<string>(),
                RepoSetup: $"""
                install -m 0755 -d /etc/apt/keyrings
                curl -fsSL https://repo.fortinet.com/repo/forticlient/7.4/{(os == TargetOs.Debian ? "debian" : "ubuntu")}/DEB-GPG-KEY \
                  | gpg --dearmor -o /etc/apt/keyrings/fortinet.gpg
                chmod 0644 /etc/apt/keyrings/fortinet.gpg
                echo "deb [signed-by=/etc/apt/keyrings/fortinet.gpg] https://repo.fortinet.com/repo/forticlient/7.4/{(os == TargetOs.Debian ? "debian" : "ubuntu")}/ stable non-free" \
                  > /etc/apt/sources.list.d/forticlient.list
                apt-get update -y
                """,
                Note: note),

            OsFamily.RedHat => new LinuxAppRecipe("FortiClient VPN", new[] { "forticlient" }, Array.Empty<string>(),
                RepoSetup: """
                cat > /etc/yum.repos.d/forticlient.repo <<'ISOFORGE_EOF'
                [forticlient]
                name=Forticlient
                baseurl=https://repo.fortinet.com/repo/forticlient/7.4/centos/9/os/x86_64/
                enabled=1
                gpgcheck=1
                gpgkey=https://repo.fortinet.com/repo/forticlient/7.4/centos/9/os/x86_64/RPM-GPG-KEY-fortinet
                ISOFORGE_EOF
                """,
                Note: note),

            // openSUSE e Arch: a Fortinet não publica pacote — usa o cliente livre openfortivpn.
            _ => new LinuxAppRecipe("openfortivpn", new[] { "openfortivpn" }, Array.Empty<string>(),
                Note: "A Fortinet não publica FortiClient para esta distro; o IsoForge instala o " +
                      "openfortivpn (cliente SSL-VPN livre, compatível com FortiGate).")
        };
    }

    // ------------------------------------------------------------------
    // Adobe Reader -> leitor de PDF nativo (a Adobe descontinuou o Reader para Linux)
    // ------------------------------------------------------------------
    static LinuxAppRecipe PdfReader(OsFamily family, LinuxDesktop desktop)
    {
        bool kde = desktop == LinuxDesktop.Kde;
        var pkg = family switch
        {
            OsFamily.Suse => kde ? "okular" : "evince",
            _ => kde ? "okular" : "evince"
        };
        return new LinuxAppRecipe(
            kde ? "Okular (leitor de PDF)" : "Evince (leitor de PDF)",
            new[] { pkg },
            Array.Empty<string>(),
            Note: "A Adobe descontinuou o Acrobat Reader para Linux em 2013. O IsoForge instala o " +
                  (kde ? "Okular" : "Evince") + ", leitor de PDF padrão do ambiente gráfico.");
    }

    // ------------------------------------------------------------------
    // Google Chrome — repositório oficial do Google
    // ------------------------------------------------------------------
    static LinuxAppRecipe Chrome(OsFamily family) => family switch
    {
        OsFamily.Debian => new LinuxAppRecipe("Google Chrome", new[] { "google-chrome-stable" }, Array.Empty<string>(),
            RepoSetup: """
            install -m 0755 -d /etc/apt/keyrings
            curl -fsSL https://dl.google.com/linux/linux_signing_key.pub | gpg --dearmor -o /etc/apt/keyrings/google-chrome.gpg
            chmod 0644 /etc/apt/keyrings/google-chrome.gpg
            echo "deb [arch=amd64 signed-by=/etc/apt/keyrings/google-chrome.gpg] https://dl.google.com/linux/chrome/deb/ stable main" \
              > /etc/apt/sources.list.d/google-chrome.list
            apt-get update -y
            """),

        OsFamily.RedHat => new LinuxAppRecipe("Google Chrome", new[] { "google-chrome-stable" }, Array.Empty<string>(),
            RepoSetup: """
            cat > /etc/yum.repos.d/google-chrome.repo <<'ISOFORGE_EOF'
            [google-chrome]
            name=google-chrome
            baseurl=https://dl.google.com/linux/chrome/rpm/stable/x86_64
            enabled=1
            gpgcheck=1
            gpgkey=https://dl.google.com/linux/linux_signing_key.pub
            ISOFORGE_EOF
            """),

        OsFamily.Suse => new LinuxAppRecipe("Google Chrome", new[] { "google-chrome-stable" }, Array.Empty<string>(),
            RepoSetup: """
            rpm --import https://dl.google.com/linux/linux_signing_key.pub || true
            zypper --non-interactive addrepo --refresh https://dl.google.com/linux/chrome/rpm/stable/x86_64 google-chrome || true
            zypper --non-interactive --gpg-auto-import-keys refresh || true
            """),

        // No Arch o Chrome fica no AUR; o Chromium (mesmo motor, código aberto) está no repo oficial.
        _ => new LinuxAppRecipe("Chromium", new[] { "chromium" }, Array.Empty<string>(),
            Note: "No Arch o Google Chrome só existe no AUR; o IsoForge instala o Chromium " +
                  "(mesmo motor Blink) a partir do repositório oficial.")
    };

    // ------------------------------------------------------------------
    // Mozilla Firefox — repositório oficial da Mozilla no Debian/Ubuntu (evita o snap)
    // ------------------------------------------------------------------
    static LinuxAppRecipe Firefox(OsFamily family) => family switch
    {
        OsFamily.Debian => new LinuxAppRecipe("Mozilla Firefox", new[] { "firefox" }, Array.Empty<string>(),
            RepoSetup: """
            install -m 0755 -d /etc/apt/keyrings
            curl -fsSL https://packages.mozilla.org/apt/repo-signing-key.gpg -o /etc/apt/keyrings/packages.mozilla.org.asc
            chmod 0644 /etc/apt/keyrings/packages.mozilla.org.asc
            echo "deb [signed-by=/etc/apt/keyrings/packages.mozilla.org.asc] https://packages.mozilla.org/apt mozilla main" \
              > /etc/apt/sources.list.d/mozilla.list
            cat > /etc/apt/preferences.d/mozilla <<'ISOFORGE_EOF'
            Package: *
            Pin: origin packages.mozilla.org
            Pin-Priority: 1000
            ISOFORGE_EOF
            apt-get update -y
            """,
            Note: "Instalado como .deb do repositório oficial da Mozilla (no Ubuntu, evita a versão snap)."),

        OsFamily.Suse => new LinuxAppRecipe("Mozilla Firefox", new[] { "MozillaFirefox" }, Array.Empty<string>()),
        _ => new LinuxAppRecipe("Mozilla Firefox", new[] { "firefox" }, Array.Empty<string>())
    };

    // ------------------------------------------------------------------
    // Notepad++ -> Geany (editor leve equivalente, presente em todos os repositórios)
    // ------------------------------------------------------------------
    static LinuxAppRecipe TextEditor(OsFamily family) => new(
        "Geany (editor de texto)",
        new[] { "geany" },
        Array.Empty<string>(),
        Note: "O Notepad++ é exclusivo do Windows. O IsoForge instala o Geany — editor leve com " +
              "realce de sintaxe e abas, o equivalente mais próximo disponível em todas as distros.");

    // ------------------------------------------------------------------
    // Visual C++ Redistributable -> toolchain/bibliotecas de runtime da distro
    // ------------------------------------------------------------------
    static LinuxAppRecipe BuildTools(OsFamily family)
    {
        var pkgs = family switch
        {
            OsFamily.Debian => new[] { "build-essential" },
            OsFamily.RedHat => new[] { "gcc", "gcc-c++", "make" },
            OsFamily.Suse => new[] { "gcc", "gcc-c++", "make" },
            _ => new[] { "base-devel" }
        };
        return new LinuxAppRecipe(
            "Ferramentas de compilação (runtime)",
            pkgs,
            Array.Empty<string>(),
            Note: "O Visual C++ Redistributable não existe no Linux: as bibliotecas de runtime já vêm " +
                  "com o sistema. O equivalente instalado é a toolchain de compilação (" + string.Join(", ", pkgs) + ").");
    }
}
