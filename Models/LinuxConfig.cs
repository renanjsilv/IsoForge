namespace IsoForge.Models;

/// <summary>Como o disco de destino é particionado na instalação desassistida do Linux.</summary>
public enum LinuxDiskMode
{
    /// <summary>Usa o disco inteiro com partições simples (ESP + raiz ext4).</summary>
    EntireDisk,
    /// <summary>Usa o disco inteiro com LVM (raiz em volume lógico).</summary>
    EntireDiskLvm,
    /// <summary>Usa o disco inteiro com LVM sobre LUKS (criptografia total).</summary>
    EntireDiskEncrypted
}

/// <summary>Ambiente gráfico instalado (para as distros que perguntam durante a instalação).</summary>
public enum LinuxDesktop
{
    /// <summary>Mantém o ambiente padrão da ISO escolhida.</summary>
    Default,
    Gnome,
    Kde,
    Xfce,
    /// <summary>Sem interface gráfica (servidor).</summary>
    None
}

/// <summary>Como a personalização é entregue junto da ISO oficial da distro.</summary>
public enum LinuxDeliveryMode
{
    /// <summary>
    /// Reempacota a ISO oficial com o arquivo de resposta embutido e o bootloader já apontando
    /// para ele — uma única ISO, boot e instala sozinho.
    /// </summary>
    Repack,
    /// <summary>
    /// Gera uma segunda ISO pequena ("seed") com o arquivo de resposta. A ISO oficial continua
    /// intacta; as duas são anexadas juntas na VM/máquina. É o caminho oficial do Ubuntu (cidata).
    /// </summary>
    SeedIso
}

/// <summary>
/// Configurações específicas do Linux. Só usadas quando <see cref="BuildConfig.Os"/> é uma distro;
/// no Windows ficam ignoradas (mas continuam salvas, para o usuário alternar sem perder nada).
/// </summary>
public class LinuxConfig
{
    /// <summary>Entrega: reempacotar a ISO ou gerar uma ISO "seed" separada.</summary>
    public LinuxDeliveryMode Delivery { get; set; } = LinuxDeliveryMode.Repack;

    /// <summary>Fuso horário no formato IANA (ex.: America/Sao_Paulo).</summary>
    public string Timezone { get; set; } = "America/Sao_Paulo";

    /// <summary>Locale do sistema (ex.: pt_BR.UTF-8).</summary>
    public string LocaleId { get; set; } = "pt_BR.UTF-8";

    /// <summary>Layout de teclado do console/X (ex.: br).</summary>
    public string KeyboardLayout { get; set; } = "br";

    /// <summary>Variante do layout (ex.: abnt2). Vazio = padrão do layout.</summary>
    public string KeyboardVariant { get; set; } = "abnt2";

    /// <summary>Nome completo (comentário GECOS) do usuário criado.</summary>
    public string FullName { get; set; } = "";

    /// <summary>Particionamento do disco de destino.</summary>
    public LinuxDiskMode DiskMode { get; set; } = LinuxDiskMode.EntireDisk;

    /// <summary>Senha do LUKS quando o disco é criptografado.</summary>
    public string DiskPassword { get; set; } = "";

    /// <summary>
    /// Disco a instalar (ex.: /dev/sda). Vazio = escolhe o primeiro disco fixo automaticamente
    /// (nunca o pendrive de boot), equivalente à seleção automática do Windows.
    /// </summary>
    public string TargetDisk { get; set; } = "";

    /// <summary>Instala o servidor OpenSSH (acesso remoto ao terminal).</summary>
    public bool InstallSshServer { get; set; }

    /// <summary>Chave pública autorizada para o usuário criado (opcional).</summary>
    public string SshAuthorizedKey { get; set; } = "";

    /// <summary>Login automático do usuário criado no primeiro boot.</summary>
    public bool AutoLogin { get; set; }

    /// <summary>Deixa a conta root sem senha (acesso administrativo só via sudo) — padrão do Ubuntu.</summary>
    public bool DisableRootPassword { get; set; } = true;

    /// <summary>Instalação mínima (menos aplicativos de fábrica).</summary>
    public bool MinimalInstall { get; set; }

    /// <summary>Instala drivers proprietários (NVIDIA, firmware) e codecs multimídia.</summary>
    public bool ProprietaryDrivers { get; set; }

    /// <summary>Atualiza os pacotes durante/logo após a instalação.</summary>
    public bool UpdateDuringInstall { get; set; } = true;

    /// <summary>Habilita Flatpak + repositório Flathub.</summary>
    public bool EnableFlatpak { get; set; }

    /// <summary>Ambiente gráfico (quando a distro permite escolher).</summary>
    public LinuxDesktop Desktop { get; set; } = LinuxDesktop.Default;

    /// <summary>Pacotes extras separados por espaço, instalados junto dos aplicativos escolhidos.</summary>
    public string ExtraPackages { get; set; } = "";

    // --- Otimização (equivalente ao debloat do Windows) ---
    /// <summary>Remove os jogos e utilitários de fábrica do ambiente gráfico.</summary>
    public bool RemoveDefaultGames { get; set; }
    /// <summary>Desativa a coleta de dados/relatórios de erro (apport, whoopsie, ubuntu-report, popcon).</summary>
    public bool DisableTelemetry { get; set; }
    /// <summary>Remove o snapd (Ubuntu/Mint) e prefere pacotes .deb nativos.</summary>
    public bool RemoveSnap { get; set; }
    /// <summary>Desativa as mensagens promocionais do MOTD (Ubuntu Pro/motd-news).</summary>
    public bool DisableMotdAds { get; set; }

    public LinuxConfig Clone() => (LinuxConfig)MemberwiseClone();
}
