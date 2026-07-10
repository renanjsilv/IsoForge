using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace IsoForge.Models;

public enum AppKind
{
    Generic,
    Office
}

/// <summary>Tema padrão do Windows aplicado no 1º logon.</summary>
public enum WindowsThemeMode
{
    /// <summary>Não altera o tema (mantém o padrão do Windows).</summary>
    Default,
    /// <summary>Tema claro (branco).</summary>
    Light,
    /// <summary>Tema escuro.</summary>
    Dark
}

/// <summary>Quando a tela de seleção de unidade aparece.</summary>
public enum UnitSelectionMethod
{
    /// <summary>No 1º logon do usuário: mostra a tela, renomeia e reinicia uma vez (sem auditoria).</summary>
    FirstLogon,
    /// <summary>Antes do usuário, no modo de auditoria (reinicia para o OOBE depois de renomear).</summary>
    Audit
}

/// <summary>
/// Como a máquina se comporta no primeiro boot.
/// </summary>
public enum DeploymentMode
{
    /// <summary>Cria o usuário local, faz logon automático e instala os apps no 1º logon (fluxo clássico).</summary>
    LocalAccount,
    /// <summary>
    /// O 1º boot mostra o OOBE com login corporativo/estudante (Entra ID): WiFi + e-mail + ingresso no Entra ID.
    /// O usuário local (admin) é criado nos bastidores (SetupComplete, como SYSTEM) e os apps são instalados
    /// antes do OOBE, também como SYSTEM (o usuário Entra entra como padrão, sem admin).
    /// </summary>
    EntraId
}

public class AppEntry
{
    public string Name { get; set; } = "";
    public string InstallerPath { get; set; } = "";
    public string SilentArgs { get; set; } = "";
    public AppKind Kind { get; set; } = AppKind.Generic;
    // true = o instalador precisa de internet no 1º logon (ex.: online installer). Nesse caso o
    // install.cmd espera uma conexão antes de instalar (mostra uma mensagem pedindo para conectar).
    public bool RequiresInternet { get; set; }
}

public class VpnTunnel
{
    public string Name { get; set; } = "";
    public string RemoteGateway { get; set; } = "";   // IP ou host do gateway IPsec
    public string PresharedKey { get; set; } = "";     // chave pré-compartilhada (PSK)
}

/// <summary>Autenticação de usuário (XAuth) do túnel IPsec.</summary>
public enum VpnXAuthMode
{
    /// <summary>Pede usuário/senha ao conectar (não salva).</summary>
    Prompt,
    /// <summary>Salva usuário/senha na configuração.</summary>
    Save,
    /// <summary>Sem autenticação de usuário.</summary>
    Disabled
}

public class UnitEntry
{
    public string Name { get; set; } = "";     // rótulo exibido (ex.: Matriz)
    public string Prefix { get; set; } = "";   // prefixo do hostname (ex.: MTZ)
}

public class BuildConfig
{
    // ISO
    public string SourceIsoPath { get; set; } = "";
    public string OutputIsoPath { get; set; } = "";
    // Caminho temporário do oscdimg embutido (re-detectado a cada execução) — não persistir.
    [JsonIgnore] public string OscdimgPath { get; set; } = "";
    public bool NoPromptBoot { get; set; }

    // Seleção automática de disco: no WinPE, escolhe o 1º disco FIXO não-USB, particiona
    // e instala sozinho (nunca o pendrive). Se não achar disco seguro, cai na seleção manual.
    public bool AutoSelectDisk { get; set; }

    // Pula a tela de configuração de WiFi no OOBE (HideWirelessSetupInOOBE=true).
    // Atenção: no modo Entra ID o WiFi é necessário para o ingresso (a não ser via cabo).
    public bool SkipWifiSetup { get; set; }

    // Conexão automática ao Wi-Fi no 1º logon: gera um perfil WLAN (SSID + senha) e conecta
    // via netsh, para a máquina já subir com internet. Senha embutida na ISO — trate como sensível.
    public bool AutoConnectWifi { get; set; }
    public string WifiSsid { get; set; } = "";
    public string WifiPassword { get; set; } = "";

    // Modo de implantação (comportamento do 1º boot)
    public DeploymentMode Mode { get; set; } = DeploymentMode.LocalAccount;

    // Modo Entra ID: remover do grupo Administradores o(s) usuário(s) Entra presentes
    // no 1º logon (o usuário que ingressou vira padrão; o usuário local continua admin).
    public bool DemoteEntraJoiner { get; set; } = true;

    // Usuário local
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public bool PasswordNeverExpires { get; set; } = true;
    public bool IsAdministrator { get; set; } = true;
    public bool AutoLogonOnce { get; set; } = true;

    // Sistema
    public string ComputerName { get; set; } = "";
    public string ProductKey { get; set; } = "";
    public bool BypassHardwareChecks { get; set; } = true;
    public string Locale { get; set; } = "pt-BR";
    public string InputLocale { get; set; } = "0416:00000416"; // Português (Brasil) ABNT2

    // Seleção de unidade (define o nome da máquina: prefixo + nº de série)
    public bool UseUnitSelection { get; set; }
    // Padrão: no 1º logon (sem auditoria). Alternativa: modo de auditoria (fluxo antigo).
    public UnitSelectionMethod UnitMethod { get; set; } = UnitSelectionMethod.FirstLogon;
    // Exemplos genéricos; edite/adicione as suas unidades (ficam salvas localmente).
    public ObservableCollection<UnitEntry> Units { get; set; } = new()
    {
        new UnitEntry { Name = "Matriz", Prefix = "MTZ" },
        new UnitEntry { Name = "Filial", Prefix = "FIL" },
    };

    // Aparência
    public string WallpaperPath { get; set; } = "";        // imagem definida como papel de parede padrão
    public string LockScreenPath { get; set; } = "";       // imagem da tela de bloqueio / login
    // Tema padrão do Windows (claro/escuro) aplicado no 1º logon e para novos usuários.
    public WindowsThemeMode WindowsTheme { get; set; } = WindowsThemeMode.Default;

    // FortiClient VPN — vazio por padrão; adicione os seus túneis (ficam salvos localmente).
    public ObservableCollection<VpnTunnel> VpnTunnels { get; set; } = new();
    // Alternativa confiável: .reg exportado de um FortiClient já configurado (importado no 1º logon).
    public string FortiClientRegImportPath { get; set; } = "";
    // Versão do instalador do FortiClient: false = 7.4.1 (MSI offline); true = mais recente (oficial Fortinet).
    public bool FortiClientLatest { get; set; }

    // Import dos túneis digitados via texto (FCConfig XML). Padrão DESLIGADO: esse método
    // pode instabilizar o FortiClient. O método confiável é o .reg (Capturar deste PC).
    public bool VpnUseTextImport { get; set; }

    // Autenticação de usuário (XAuth) dos túneis. Padrão: pedir no login.
    public VpnXAuthMode VpnXAuth { get; set; } = VpnXAuthMode.Prompt;
    public string XAuthUsername { get; set; } = "";   // usado só no modo Salvar
    public string XAuthPassword { get; set; } = "";   // usado só no modo Salvar

    // Scripts
    public bool UseCustomUnattend { get; set; }
    public string CustomUnattendPath { get; set; } = "";
    public string PostScriptPath { get; set; } = "";

    // Aplicativos
    public ObservableCollection<AppEntry> Apps { get; set; } = new();
    public string OfficeConfigXml { get; set; } = DefaultOfficeConfig;
    // Idioma do Office (ID do ODT: pt-br, en-us, es-es, pt-pt...).
    public string OfficeLanguage { get; set; } = "pt-br";

    // Office offline: instala do arquivo local (sem baixar da internet no 1º logon).
    // OfficeSourceFolder contém setup.exe + a pasta Office\Data (gerada por setup.exe /download).
    public bool OfficeOffline { get; set; }
    public string OfficeSourceFolder { get; set; } = "";

    // Imagem "golden": usar um install.wim capturado (Windows + apps já pré-instalados).
    public bool UseCapturedWim { get; set; }
    public string CapturedWimPath { get; set; } = "";

    // Uso interno (teste no Windows Sandbox): mostra a seleção de unidade mas NÃO reinicia
    // (o Sandbox não suporta reboot). Definido pelo PrepareSandbox, não pela UI.
    public bool SandboxTest { get; set; }

    // Uso interno: quando true, gera o autounattend/scripts da ISO de REFERÊNCIA
    // (disco automático + modo de auditoria + golden.cmd que instala tudo e faz sysprep).
    // Definido pelo orquestrador automático de imagem golden, não pela UI.
    public bool GoldenReference { get; set; }

    // Parâmetros da VM headless usada na geração automática da imagem golden.
    public int GoldenVmMemoryGB { get; set; } = 4;
    public int GoldenVmDiskGB { get; set; } = 64;
    public int GoldenTimeoutMinutes { get; set; } = 90;

    /// <summary>Cópia rasa com coleções independentes (para derivar configs de build).</summary>
    public BuildConfig Clone()
    {
        var c = (BuildConfig)MemberwiseClone();
        c.Apps = new ObservableCollection<AppEntry>(Apps.Select(a => new AppEntry
        {
            Name = a.Name, InstallerPath = a.InstallerPath, SilentArgs = a.SilentArgs, Kind = a.Kind
        }));
        c.VpnTunnels = new ObservableCollection<VpnTunnel>(VpnTunnels.Select(t => new VpnTunnel
        {
            Name = t.Name, RemoteGateway = t.RemoteGateway, PresharedKey = t.PresharedKey
        }));
        c.Units = new ObservableCollection<UnitEntry>(Units.Select(u => new UnitEntry
        {
            Name = u.Name, Prefix = u.Prefix
        }));
        return c;
    }

    public const string DefaultOfficeConfig = """
<Configuration>
  <Add OfficeClientEdition="64" Channel="Current">
    <Product ID="O365ProPlusRetail">
      <Language ID="pt-br" />
    </Product>
  </Add>
  <Display Level="Full" AcceptEULA="TRUE" />
  <Updates Enabled="TRUE" />
  <RemoveMSI />
</Configuration>
""";

    /// <summary>Gera o XML de configuração do ODT para o idioma escolhido.</summary>
    public static string BuildOfficeConfig(string languageId) =>
        "<Configuration>\n" +
        "  <Add OfficeClientEdition=\"64\" Channel=\"Current\">\n" +
        "    <Product ID=\"O365ProPlusRetail\">\n" +
        $"      <Language ID=\"{languageId}\" />\n" +
        "    </Product>\n" +
        "  </Add>\n" +
        "  <Display Level=\"Full\" AcceptEULA=\"TRUE\" />\n" +
        "  <Updates Enabled=\"TRUE\" />\n" +
        "  <RemoveMSI />\n" +
        "</Configuration>";
}
