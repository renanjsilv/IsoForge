using System;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IsoForge.Core;
using IsoForge.Core.Linux;
using IsoForge.Models;

namespace IsoForge;

/// <summary>
/// Parte da janela principal responsável pelo sistema operacional de destino: adaptação da
/// interface (Windows × Linux), troca de sistema, identificação automática da ISO selecionada
/// e leitura/escrita das configurações específicas de Linux.
/// </summary>
public partial class MainWindow
{
    // ------------------------------------------------------------------
    // Fundo animado
    // ------------------------------------------------------------------
    System.Windows.Media.Animation.Storyboard? _ambient;

    /// <summary>Os pinceis vivos do fundo do Shell, tingidos pela marca do sistema.</summary>
    readonly Ui.BrandWash _fundoMarca = new();
    bool _primeiroDesenho = true;

    /// <summary>
    /// Liga o fundo animado da coluna de conteúdo. O storyboard precisa ser parado
    /// no fechamento: sem isso cada abertura da janela soma relógios perpétuos.
    /// </summary>
    void StartBackdrop()
    {
        try
        {
            Backdrop.ApplyTemplate();
            _fundoMarca.LigarEm(Backdrop);
            _ambient = (System.Windows.Media.Animation.Storyboard)FindResource("AmbientSb");
            _ambient.Begin(Backdrop, Backdrop.Template, isControllable: true);

            LigarFundoDaBarra(true);

            // Fundo decorativo não pode consumir CPU quando ninguém está olhando:
            // pausa ao perder o foco ou minimizar, retoma ao voltar.
            Deactivated += (_, __) => PausarTodosOsFundos(true);
            Activated += (_, __) => PausarTodosOsFundos(WindowState == WindowState.Minimized);
            StateChanged += (_, __) => PausarTodosOsFundos(WindowState == WindowState.Minimized);

            Closed += (_, __) =>
            {
                try { _ambient?.Stop(Backdrop); _ambient?.Remove(Backdrop); } catch { }
                _ambient = null;
            };
        }
        catch { /* o fundo é decorativo: se falhar, a janela continua utilizável */ }
    }

    void PauseBackdrop(bool pausar)
    {
        try
        {
            // Ver OsPickerView.PauseAmbient para a medicao: parar o relogio e a UNICA
            // das tres formas que realmente corta o custo (4% contra 45% e 57%).
            if (pausar)
            {
                if (_ambient == null) return;
                _ambient.Stop(Backdrop);
                _ambient.Remove(Backdrop);
                _ambient = null;
                return;
            }

            if (_ambient != null) return;
            Backdrop.ApplyTemplate();
            _ambient = (System.Windows.Media.Animation.Storyboard)FindResource("AmbientSb");
            _ambient.Begin(Backdrop, Backdrop.Template, isControllable: true);
            Backdrop.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(
                    0, 1, TimeSpan.FromMilliseconds(350)));
        }
        catch { }
    }

    System.Windows.Media.Animation.Storyboard? _sidebarSb;

    /// <summary>
    /// Liga/desliga o fundo da barra lateral. Parar o relogio (e nao pausar) pelo
    /// mesmo motivo medido em <see cref="PauseBackdrop"/>: e a unica forma que
    /// realmente devolve a CPU.
    /// </summary>
    void LigarFundoDaBarra(bool ligar)
    {
        try
        {
            if (!ligar)
            {
                if (_sidebarSb == null) return;
                _sidebarSb.Stop(SidebarBackdrop);
                _sidebarSb.Remove(SidebarBackdrop);
                _sidebarSb = null;
                return;
            }

            if (_sidebarSb != null) return;
            SidebarBackdrop.ApplyTemplate();
            _sidebarSb = (System.Windows.Media.Animation.Storyboard)FindResource("SidebarSb");
            _sidebarSb.Begin(SidebarBackdrop, SidebarBackdrop.Template, isControllable: true);
        }
        catch { }
    }

    /// <summary>
    /// Pausa TODOS os fundos animados da janela — Shell, escolha de sistema e
    /// abertura. So o do Shell pausava, e como a escolha de sistema e a primeira
    /// tela de toda abertura, o app gastava ~70% de um nucleo em segundo plano
    /// antes mesmo de o usuario escolher alguma coisa.
    ///
    /// O Shell continua com regra propria (ShowPicker o pausa enquanto a camada
    /// esta em cima), entao aqui ele so e retomado se nao houver camada por cima.
    /// </summary>
    void PausarTodosOsFundos(bool pausar)
    {
        var camadaPorCima = PickerLayer.Visibility == Visibility.Visible
                            || SplashLayer.Visibility == Visibility.Visible;

        PauseBackdrop(pausar || camadaPorCima);
        _picker?.PauseAmbient(pausar);
        _splash?.PauseAmbient(pausar);

        // A barra fica visivel em TODAS as telas do Shell, mas nao atras da camada
        // de escolha — ali ela esta coberta e so gastaria CPU.
        LigarFundoDaBarra(!pausar && !camadaPorCima);
    }

    // ------------------------------------------------------------------
    // Aplicativos identificados pelo id do catálogo (vale para os dois sistemas)
    // ------------------------------------------------------------------

    /// <summary>O FortiClient "mais recente" e o 7.4.1 são o mesmo app do catálogo.</summary>
    static AppId Canonical(AppId id) => id == AppId.FortiClientLatest ? AppId.FortiClient : id;

    /// <summary>A entrada corresponde a este app do catálogo?</summary>
    static bool Matches(AppEntry a, AppId id)
    {
        var canonical = Canonical(id);
        if (!string.IsNullOrEmpty(a.CatalogId))
            return Enum.TryParse<AppId>(a.CatalogId, out var parsed) && Canonical(parsed) == canonical;

        // Configurações salvas antes do CatalogId: reconhece pelo nome usado no Windows.
        return a.Name == LinuxAppCatalog.WindowsName(canonical)
            || (canonical == AppId.OfficeOdt && a.Kind == AppKind.Office)
            || (canonical == AppId.VcRedist && a.Name == "Visual C++ 2015-2022 (x64)");
    }

    bool HasApp(AppId id) => _config.Apps.Any(a => Matches(a, id));

    void RemoveApp(AppId id)
    {
        foreach (var app in _config.Apps.Where(a => Matches(a, id)).ToList())
            _config.Apps.Remove(app);
    }

    /// <summary>
    /// Adiciona o app no modo Linux: não há instalador para baixar — o programa vem dos
    /// repositórios da distro e é instalado no primeiro boot.
    /// </summary>
    bool AddLinuxApp(AppId id)
    {
        var canonical = Canonical(id);
        if (HasApp(canonical)) return true;

        var recipe = LinuxAppCatalog.Recipe(canonical, _config);
        if (recipe is not { Installable: true })
        {
            Caixa.Informar(this, $"{LinuxAppCatalog.WindowsName(canonical)} não tem equivalente instalável no {OsCatalog.NameOf(_config.Os)}.");
            return false;
        }

        _config.Apps.Add(new AppEntry
        {
            Name = recipe.DisplayName,
            CatalogId = canonical.ToString(),
            Kind = canonical == AppId.OfficeOdt ? AppKind.Office : AppKind.Generic
        });

        AppendLog($"{recipe.DisplayName} adicionado (pacote da distro, instalado no 1º boot).");
        if (!string.IsNullOrWhiteSpace(recipe.Note)) AppendLog($"  {recipe.Note}");
        return true;
    }

    /// <summary>Rótulo curto do card quando o Linux está selecionado.</summary>
    string LinuxCardLabel(AppId id) => id switch
    {
        AppId.SevenZip => "7-Zip (p7zip)",
        AppId.AdobeReader => "Leitor de PDF",
        AppId.NotepadPlus => "Geany (editor)",
        AppId.VcRedist => "Compiladores",
        _ => LinuxAppCatalog.Recipe(id, _config)?.DisplayName ?? LinuxAppCatalog.WindowsName(id)
    };

    /// <summary>Cards da aba Aplicativos e o app do catálogo que cada um representa.</summary>
    IEnumerable<(System.Windows.Controls.Primitives.ToggleButton Card, AppId Id, string WindowsLabel)> AppCards()
    {
        yield return (CardOffice, AppId.OfficeOdt, "Office 365");
        yield return (CardAnyDesk, AppId.AnyDesk, "AnyDesk");
        yield return (CardSevenZip, AppId.SevenZip, "7-Zip");
        yield return (CardForti, AppId.FortiClient, "FortiClient");
        yield return (CardAdobe, AppId.AdobeReader, "Adobe Reader");
        yield return (CardChrome, AppId.Chrome, "Google Chrome");
        yield return (CardFirefox, AppId.Firefox, "Firefox");
        yield return (CardNotepad, AppId.NotepadPlus, "Notepad++");
        yield return (CardVcRedist, AppId.VcRedist, "Visual C++");
    }

    // ------------------------------------------------------------------
    // Adaptação da interface ao sistema escolhido
    // ------------------------------------------------------------------
    void ApplyOsToUi()
    {
        // A cor da marca alimenta o Shell inteiro (cartoes, botoes, barra lateral).
        // Antes da UI: assim os DynamicResource ja resolvem no valor novo.
        OsAccentService.Apply(_config.Os, _config.AppDarkTheme);

        // O fundo do Shell recebe a MESMA marca do tabuleiro de escolha, a 45% da
        // intensidade: aqui o usuario le formularios. E o que faz a passagem entre as
        // duas telas parecer uma so — o fundo nao muda de cor no caminho.
        _fundoMarca.Tingir(OsCardVm.ToColor(OsCatalog.Get(_config.Os).Accent),
                           _config.AppDarkTheme, forca: 0.45,
                           ms: _primeiroDesenho ? 0 : 480);
        _primeiroDesenho = false;

        var os = OsCatalog.Get(_config.Os);
        bool linux = os.IsLinux;
        var win = linux ? Visibility.Collapsed : Visibility.Visible;
        var lin = linux ? Visibility.Visible : Visibility.Collapsed;

        Title = $"IsoForge — Personalizador de ISO do {os.Name}";
        TxtSidebarSubtitle.Text = $"{os.Name} ISO Studio";
        BtnChangeOs.Content = os.Name;

        // --- Aba ISO ---
        LblSourceIso.Text = $"ISO do {os.Name}:";
        GroupDiskWindows.Visibility = win;
        GroupLinuxDelivery.Visibility = lin;
        ChkSkipWifi.Visibility = win;
        GroupFirstBoot.Header = linux ? "Rede no primeiro boot" : "Primeiro boot (OOBE)";
        if (linux) UpdateDeliveryUi();

        // --- Aba Sistema e usuário ---
        GroupDeployMode.Visibility = win;
        GroupWindowsSystem.Visibility = win;
        GroupLinuxSystem.Visibility = lin;
        GroupLinuxDisk.Visibility = lin;
        GroupLinuxOptions.Visibility = lin;
        GroupLocalUser.Header = linux ? "Usuário do sistema" : "Usuário local";
        ChkNeverExpires.Visibility = win;
        ChkAutoLogon.Visibility = win;
        ChkAdmin.Content = linux ? "Administrador (sudo)" : "Administrador local";
        LblComputerName.Text = linux ? "Nome da máquina:" : "Nome do computador:";

        // --- Aba Aplicativos ---
        TxtAppsIntro.Text = linux
            ? "Clique nos programas que quer na imagem. No Linux eles vêm dos repositórios oficiais " +
              "(do fabricante quando existe) e são instalados sozinhos no primeiro boot — nada é baixado agora."
            : "Clique nos programas que quer na imagem. O IsoForge baixa a versão mais recente sozinho e os " +
              "instala em silêncio no primeiro logon. Ao selecionar, as opções de cada programa aparecem abaixo.";
        LinuxAppsNote.Visibility = lin;
        BtnAddCustomApp.Visibility = win;
        if (linux) TxtLinuxAppsNote.Text = LinuxAppsSummary();

        foreach (var (card, id, windowsLabel) in AppCards())
            card.Content = linux ? LinuxCardLabel(id) : windowsLabel;

        OfficeConfigCard.Header = linux ? "Opções do LibreOffice" : "Opções do Office 365";
        PanelOfficeMode.Visibility = win;
        if (linux) PanelOfficeSource.Visibility = Visibility.Collapsed;
        TxtOfficeHint.Text = linux
            ? "O idioma escolhido define o pacote de tradução do LibreOffice instalado (ex.: libreoffice-l10n-pt-br)."
            : "Online: cada máquina baixa o Office no 1º logon (ISO menor). Offline: o Office é embutido na ISO e instala sem internet.";

        FortiConfigCard.Header = linux ? "Opções da VPN" : "Opções do FortiClient VPN";
        PanelFortiWindows.Visibility = win;
        FortiLinuxNote.Visibility = lin;
        if (linux)
            TxtFortiLinuxNote.Text =
                "O FortiClient para Linux faz apenas SSL-VPN. Os túneis IPsec digitados abaixo são convertidos " +
                "em conexões strongSwan (/etc/ipsec.d/isoforge.conf), configuradas no primeiro boot.";

        // --- Aba Drivers ---
        GroupDriversWindows.Visibility = win;
        GroupDriversLinux.Visibility = lin;
        if (linux) ChkLinuxDriversMirror.IsChecked = _config.Linux.ProprietaryDrivers;

        // --- Aba Personalização ---
        LblThemeLabel.Text = linux ? "Tema do sistema:" : "Tema do Windows:";
        TxtThemeHint.Text = linux
            ? "Aplicado como padrão de todos os usuários via dconf (GNOME) no primeiro boot. 'Padrão do sistema' não altera nada."
            : "Define o tema de aplicativos e do sistema no primeiro logon (e para novos usuários). 'Padrão do sistema' não altera nada.";
        LblTaskbarLabel.Text = linux ? "Posição do dock:" : "Alinhamento da barra de tarefas:";
        TxtTaskbarHint.Text = linux
            ? "Posição da barra de aplicativos do GNOME (dash-to-dock): 'Centro' deixa embaixo, 'Esquerda' na lateral."
            : "Posição dos ícones da barra de tarefas no primeiro logon (e para novos usuários). 'Padrão do sistema' não altera nada.";
        GroupDebloatWindows.Visibility = win;
        GroupDebloatLinux.Visibility = lin;
        PanelCustomUnattend.Visibility = win;
        LblPostScript.Text = linux
            ? "Script executado no primeiro boot (.sh):"
            : "Script executado no 1º logon (.ps1, .cmd ou .bat):";
        TxtUnitHint.Text = linux
            ? "No primeiro login aparece uma tela (zenity) para escolher a unidade. O nome da máquina vira: " +
              "PREFIXO + número de série do equipamento (lido por dmidecode)."
            : "No 1º logon aparece uma tela para escolher a unidade. O nome da máquina vira: PREFIXO + número de série do equipamento.";
        ExpanderUnitMethod.Visibility = win;

        // --- Abas e ações exclusivas do Windows ---
        // IMAGEM GOLDEN (DESATIVADA): a aba está comentada em MainWindow.xaml.
        // TabGolden.Visibility = win;
        BtnSandbox.Visibility = win;
        BtnTestScript.ToolTip = linux
            ? "Salva um script que cria uma VM Hyper-V preparada para Linux e dá boot na ISO gerada."
            : "Salva um script que cria uma VM e dá boot na ISO gerada.";
        BtnDryRun.ToolTip = linux
            ? $"Gera o {os.AnswerFileName} e os scripts de pós-instalação para inspeção, sem criar a ISO."
            : "Gera o autounattend.xml e a pasta Setup para inspeção, sem criar a ISO.";

        // Se a aba ativa foi escondida, volta para a primeira.
        if (MainTabs.SelectedItem is TabItem { Visibility: Visibility.Collapsed })
            MainTabs.SelectedItem = TabIso;

        // A troca de sistema esconde/mostra cartoes INTEIROS. Sem repetir a
        // cascata, a pagina se reconfigura entre dois quadros e o usuario nao ve
        // o que mudou — ve so que "ficou diferente".
        if (MainTabs.SelectedItem is TabItem { Content: ScrollViewer { Content: Ui.CardFlow pagina } })
            Ui.CardEntrance.Play(pagina);
    }

    /// <summary>Resumo das substituições de programas feitas nesta distro.</summary>
    string LinuxAppsSummary()
    {
        var trocas = new List<string>();
        foreach (var id in new[] { AppId.OfficeOdt, AppId.AdobeReader, AppId.NotepadPlus, AppId.VcRedist, AppId.Chrome })
        {
            var recipe = LinuxAppCatalog.Recipe(id, _config);
            if (recipe == null) continue;
            var windowsName = LinuxAppCatalog.WindowsName(id);
            if (!recipe.DisplayName.Contains(windowsName, StringComparison.OrdinalIgnoreCase))
                trocas.Add($"{windowsName} → {recipe.DisplayName}");
        }

        var texto = $"Os mesmos programas do Windows, na versão de {OsCatalog.NameOf(_config.Os)}. " +
                    "AnyDesk, Chrome, Firefox e FortiClient vêm dos repositórios oficiais do fabricante; " +
                    "o que não existe para Linux é substituído pelo equivalente consagrado";
        if (trocas.Count > 0) texto += ": " + string.Join(" · ", trocas);
        texto += ". Para incluir outros pacotes, use \"Pacotes extras\" na aba Sistema e usuário.";
        return texto;
    }

    void UpdateDeliveryUi()
    {
        bool seedOk = LinuxAnswerFile.SeedSupported(_config.Os);
        RbDeliverySeed.IsEnabled = seedOk;
        if (!seedOk) RbDeliveryRepack.IsChecked = true;

        var os = OsCatalog.NameOf(_config.Os);
        TxtDeliveryHint.Text = RbDeliverySeed.IsChecked == true
            ? $"A ISO oficial do {os} não é alterada. O IsoForge gera uma segunda ISO pequena com o rótulo " +
              $"{LinuxAnswerFile.SeedLabel(_config.Os)}, que você anexa junto na VM ou grava num segundo pendrive. " +
              "O instalador a encontra sozinho."
            : seedOk
                ? $"A ISO do {os} é extraída, recebe o arquivo de resposta e volta a ser compilada com o GRUB já " +
                  "apontando para ele. Resultado: uma única ISO que instala sem nenhuma pergunta."
                : $"O instalador do {os} só lê o arquivo de resposta a partir da própria mídia, então o " +
                  "reempacotamento é obrigatório. A ISO gerada instala sem nenhuma pergunta.";
    }

    void Delivery_Changed(object sender, RoutedEventArgs e)
    {
        if (TxtDeliveryHint == null) return;
        _config.Linux.Delivery = RbDeliverySeed.IsChecked == true ? LinuxDeliveryMode.SeedIso : LinuxDeliveryMode.Repack;
        UpdateDeliveryUi();
    }

    void LinuxDisk_Changed(object sender, RoutedEventArgs e)
    {
        if (LblDiskPassword == null) return;
        var show = RbDiskCrypt.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LblDiskPassword.Visibility = show;
        TxtDiskPassword.Visibility = show;
    }

    // O mesmo ajuste aparece na aba Drivers e na aba Sistema: mantém os dois sincronizados.
    void LinuxDriversMirror_Click(object sender, RoutedEventArgs e)
    {
        if (ChkLinuxDrivers == null) return;
        ChkLinuxDrivers.IsChecked = ChkLinuxDriversMirror.IsChecked;
    }

    // ------------------------------------------------------------------
    // Troca de sistema operacional
    // ------------------------------------------------------------------
    // ------------------------------------------------------------------
    // Camada de escolha do sistema (mesma janela, não um diálogo à parte)
    // ------------------------------------------------------------------
    // ==================================================================
    // ABERTURA
    // ==================================================================

    SplashView? _splash;
    bool _splashComecou;

    /// <summary>Acesso do harness de render às duas pontas do voo da abertura.</summary>
    public (SplashView? splash, FrameworkElement? alvo) PreviewSplashParts()
        => (_splash, _picker?.BrandMark);

    /// <summary>
    /// Sobe a abertura por cima de tudo. Ela só COMEÇA quando a janela desenha o
    /// primeiro quadro (<c>ContentRendered</c>): iniciada no construtor, boa parte
    /// da animação já teria passado antes de existir tela para mostrá-la.
    /// </summary>
    void ShowSplash()
    {
        _splash = new SplashView();
        _splash.Finished += async (_, __) =>
        {
            try { await HideSplashAsync(); }
            catch (Exception ex) { AppendLog($"ERRO ao encerrar a abertura: {ex.Message}"); }
        };

        SplashLayer.Children.Add(_splash);
        SplashLayer.Visibility = Visibility.Visible;

        // A tela de baixo não toca a própria entrada: quem dá a partida é a
        // abertura, no instante em que se dissolve.
        if (_picker != null) _picker.DeferEntrance = true;

        ContentRendered += (_, __) =>
        {
            if (_splashComecou) return;
            _splashComecou = true;
            _splash?.Play();

            // Aplica o ESTADO, nao so as transicoes: Deactivated so dispara depois
            // de a janela ter sido ativada alguma vez. Uma janela que nasce em
            // segundo plano (sessao bloqueada, aberta por script) nunca recebia o
            // evento e deixava os fundos a plena carga.
            PausarTodosOsFundos(!IsActive || WindowState == WindowState.Minimized);
        };
    }

    /// <summary>
    /// Fecha a abertura levando a marca até o lugar dela na tela de baixo. Não há
    /// corte: a marca que voa pousa exatamente sobre a marca de verdade, que já
    /// está desenhada embaixo — por isso a camada some sem ninguém notar.
    /// </summary>
    async Task HideSplashAsync()
    {
        if (_splash == null) return;

        var noSeletor = _picker != null && PickerLayer.Visibility == Visibility.Visible;
        FrameworkElement? alvo = noSeletor ? _picker!.BrandMark : SidebarMark;

        await _splash.FlyToAsync(alvo, () =>
        {
            if (noSeletor) _picker!.PlayEntrance(completa: false);
            else Shell.BeginAnimation(OpacityProperty,
                     new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)));
        });

        _splash.StopAmbient();          // nada de relógio perpétuo escondido
        SplashLayer.Children.Clear();
        SplashLayer.Visibility = Visibility.Collapsed;
        _splash = null;

        // Reavalia DEPOIS que a camada sai. Enquanto a abertura estava por cima,
        // tudo o que ficava atras dela foi parado; sem esta linha, quem abre direto
        // no Shell (construtor com sistema definido) ficava com o fundo e a barra
        // mortos para sempre.
        PausarTodosOsFundos(!IsActive || WindowState == WindowState.Minimized);

        if (noSeletor) _picker!.Foco();
    }

    OsPickerView? _picker;

    /// <summary>
    /// Sobe a camada de escolha do sistema por cima do shell. O shell continua
    /// montado atrás — é isso que faz a passagem parecer uma transição de tela e
    /// não a troca de um programa por outro.
    /// </summary>
    /// <param name="primeira">
    /// true na abertura do app: aí "Cancelar" encerra. Depois, quando o usuário
    /// clica em "trocar sistema", cancelar apenas volta para o shell.
    /// </param>
    void ShowPicker(bool primeira)
    {
        CollectConfig();

        if (_picker == null)
        {
            _picker = new OsPickerView(_config.Os);
            _picker.Confirmed += async (_, __) =>
            {
                // async void por trás do evento: sem try/catch, qualquer falha aqui
                // (rede ao rebaixar apps, por exemplo) derrubaria o processo.
                try
                {
                    var escolhido = _picker!.SelectedOs;
                    await HidePickerAsync();
                    if (escolhido != _config.Os)
                        await SwitchOsAsync(escolhido, "escolha do usuário");
                }
                catch (Exception ex) { AppendLog($"ERRO ao aplicar o sistema escolhido: {ex.Message}"); }
            };
            _picker.Cancelled += async (_, __) =>
            {
                try
                {
                    if (_pickerPrimeira) Close();
                    else await HidePickerAsync();
                }
                catch (Exception ex) { AppendLog($"ERRO ao fechar a escolha de sistema: {ex.Message}"); }
            };
            PickerLayer.Children.Add(_picker);
        }
        else
        {
            _picker.Preselect(_config.Os);
            _picker.StartAmbient();
            _picker.Foco();          // sem isto as setas nao respondiam ao reabrir
        }

        _pickerPrimeira = primeira;
        PickerLayer.Visibility = Visibility.Visible;
        PickerLayer.IsHitTestVisible = true;

        // O shell fica atras da camada: sem desabilitar, o Tab caminha por controles
        // invisiveis. E o fundo animado dele continuaria girando escondido.
        Shell.IsEnabled = false;
        PauseBackdrop(true);
        LigarFundoDaBarra(false);

        if (primeira) return;   // na abertura já entra visível; aqui é reentrada

        PickerShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-22, 0, TimeSpan.FromMilliseconds(280))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        PickerLayer.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    bool _pickerPrimeira = true;

    /// <summary>
    /// Desce a camada revelando o shell. A camada sobe e some enquanto o shell
    /// cresce de 0,985 para 1 — as duas metades do mesmo movimento.
    /// </summary>
    Task HidePickerAsync()
    {
        var pronto = new TaskCompletionSource();

        // Antes de qualquer animacao: dois cliques rapidos disparavam a transicao
        // duas vezes e o primeiro Completed zerava o transform no meio da segunda.
        PickerLayer.IsHitTestVisible = false;
        Shell.IsEnabled = true;
        PauseBackdrop(false);

        var escala = new ScaleTransform(0.985, 0.985);
        Shell.RenderTransformOrigin = new Point(0.5, 0.5);
        Shell.RenderTransform = escala;
        var cresce = new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(340))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        escala.BeginAnimation(ScaleTransform.ScaleXProperty, cresce);
        escala.BeginAnimation(ScaleTransform.ScaleYProperty, cresce);

        PickerShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, -26, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });

        var some = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
        some.Completed += (_, __) =>
        {
            PickerLayer.Visibility = Visibility.Collapsed;
            _picker?.StopAmbient();          // nada de relógio perpétuo escondido
            Shell.RenderTransform = null;
            LigarFundoDaBarra(true);
            RevelarShell();
            pronto.TrySetResult();
        };
        PickerLayer.BeginAnimation(OpacityProperty, some);
        return pronto.Task;
    }

    /// <summary>
    /// O Shell aparecia INTEIRO num quadro quando a camada de escolha saía. Aqui ele
    /// se monta: a topbar desce, o rodapé sobe e o trilho de abas entra em cascata —
    /// o mesmo gesto (e o mesmo easing) dos cartões do tabuleiro que acabou de sumir.
    /// É o que faz as duas telas parecerem um movimento só.
    /// </summary>
    void RevelarShell()
    {
        Entrar(TopBar, -14, 0, 260);
        Entrar(ActionRail, 16, 40, 280);

        var i = 0;
        foreach (var item in MainTabs.Items)
        {
            if (item is not TabItem aba || aba.Visibility != Visibility.Visible) continue;
            Entrar(aba, -10, 70 + i * 26, 240);
            i++;
        }
    }

    /// <summary>
    /// Um elemento entra deslizando, com atraso. Os transforms são reaproveitados:
    /// reabrir a escolha do sistema chama isto de novo, e transform empilhado faria
    /// a segunda passagem começar de um lugar errado.
    /// </summary>
    static void Entrar(FrameworkElement alvo, double de, int atrasoMs, int ms)
    {
        if (alvo.RenderTransform is not TranslateTransform t)
            alvo.RenderTransform = t = new TranslateTransform();

        var atraso = TimeSpan.FromMilliseconds(atrasoMs);
        alvo.Opacity = 0;                       // valor BASE: é ele que vale durante o atraso
        var surge = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { BeginTime = atraso };
        surge.Completed += (_, __) => { alvo.BeginAnimation(UIElement.OpacityProperty, null); alvo.Opacity = 1; };
        alvo.BeginAnimation(UIElement.OpacityProperty, surge);

        var desliza = new DoubleAnimation(de, 0, TimeSpan.FromMilliseconds(ms))
        { BeginTime = atraso, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        desliza.Completed += (_, __) => { t.BeginAnimation(TranslateTransform.YProperty, null); t.Y = 0; };
        t.BeginAnimation(TranslateTransform.YProperty, desliza);
    }

    void ChangeOs_Click(object sender, RoutedEventArgs e) => ShowPicker(primeira: false);

    /// <summary>
    /// Troca o sistema de destino preservando tudo que o usuário já preencheu. Os aplicativos
    /// selecionados são reconstruídos para o novo sistema (no Windows, baixando os instaladores;
    /// no Linux, apontando para os pacotes da distro).
    /// </summary>
    async Task SwitchOsAsync(TargetOs newOs, string reason)
    {
        var previous = _config.Os;
        if (previous == newOs) return;

        AppendLog($"Sistema de destino: {OsCatalog.NameOf(previous)} → {OsCatalog.NameOf(newOs)} ({reason}).");

        var ids = _config.Apps
            .Select(a => Enum.TryParse<AppId>(a.CatalogId, out var v) ? (AppId?)Canonical(v) : null)
            .Where(v => v != null).Select(v => v!.Value).Distinct().ToList();
        var custom = _config.Apps.Where(a => string.IsNullOrEmpty(a.CatalogId)).ToList();

        _config.Apps.Clear();
        _config.Os = newOs;
        bool toLinux = _config.IsLinux;
        bool familyChanged = OsCatalog.IsLinux(previous) != toLinux;

        foreach (var id in ids)
        {
            if (toLinux) AddLinuxApp(id);
            else await AddAutoAsync(id);
        }

        // Instaladores avulsos (.exe/.msi) não existem no Linux.
        foreach (var app in custom)
        {
            if (toLinux)
                AppendLog($"\"{app.Name}\" foi removido: instaladores do Windows não se aplicam ao Linux. " +
                          "Use \"Pacotes extras\" para incluir pacotes da distro.");
            else
                _config.Apps.Add(app);
        }

        // A ISO selecionada provavelmente é do sistema anterior.
        if (familyChanged && !string.IsNullOrWhiteSpace(TxtSourceIso.Text))
        {
            var identity = IsoInspector.Identify(TxtSourceIso.Text);
            if (identity?.Os != newOs)
            {
                AppendLog($"ISO de origem limpa: \"{Path.GetFileName(TxtSourceIso.Text)}\" não é do {OsCatalog.NameOf(newOs)}.");
                TxtSourceIso.Text = "";
                TxtOutputIso.Text = "";
                IsoIdentityBanner.Visibility = Visibility.Collapsed;
            }
        }

        ApplyOsToUi();
        ApplyLinuxConfigToUi();
        RefreshAppCards();
        UpdateUnitPreview();
        SettingsStore.Save(_config);
    }

    // ------------------------------------------------------------------
    // Identificação automática da ISO
    // ------------------------------------------------------------------

    /// <summary>Caminho sugerido para a ISO de saída, a partir do nome da ISO de origem.</summary>
    string SuggestedOutputPath(string sourceIso)
    {
        var dir = Path.GetDirectoryName(sourceIso)!;
        var name = Path.GetFileNameWithoutExtension(sourceIso);
        var suffix = _config.IsLinux && LinuxAnswerFile.EffectiveDelivery(_config) == LinuxDeliveryMode.SeedIso
            ? "_SEED"
            : "_PERSONALIZADA";
        return Path.Combine(dir, $"{name}{suffix}.iso");
    }

    /// <summary>
    /// Lê a ISO escolhida, mostra o que ela contém e — se pertencer a outro sistema — corrige
    /// automaticamente a seleção, sem perder o que já estava preenchido.
    /// </summary>
    async Task IdentifyIsoAndFixOsAsync(string isoPath)
    {
        IsoIdentity? identity = null;
        try
        {
            Status("Identificando a ISO...");
            identity = await Task.Run(() => IsoInspector.Identify(isoPath));
        }
        catch (Exception ex) { AppendLog($"Não consegui ler a ISO: {ex.Message}"); }
        finally { Status("Pronto"); }

        if (identity == null)
        {
            ShowIsoBanner(false,
                "Não consegui identificar o conteúdo desta ISO (ela pode estar corrompida ou não ser uma imagem ISO 9660). " +
                $"Vou tratá-la como {OsCatalog.NameOf(_config.Os)}.");
            return;
        }

        if (identity.Os == null)
        {
            var extra = identity.Supported
                ? ""
                : $" Reconheci como {identity.UnsupportedName}, que o IsoForge ainda não personaliza.";
            ShowIsoBanner(false,
                $"ISO não reconhecida (rótulo: {identity.Label}).{extra} " +
                $"Ela será tratada como {OsCatalog.NameOf(_config.Os)} — confira se é isso mesmo.");
            AppendLog($"ISO não reconhecida: rótulo \"{identity.Label}\".");
            return;
        }

        var detected = identity.Os.Value;
        var mismatch = IsoInspector.Mismatch(_config.Os, identity);
        if (mismatch == null)
        {
            ShowIsoBanner(true, $"ISO reconhecida: {identity.Description} (rótulo: {identity.Label}). " +
                                $"Confere com o sistema selecionado.");
            return;
        }

        // Correção automática: a ISO manda, porque é o conteúdo real.
        var antes = OsCatalog.NameOf(_config.Os);
        await SwitchOsAsync(mismatch.Value, "ISO de outro sistema selecionada");
        TxtOutputIso.Text = SuggestedOutputPath(isoPath);

        ShowIsoBanner(true,
            $"Esta ISO é do {identity.Description}, mas o sistema selecionado era {antes}. " +
            $"Corrigi automaticamente para {OsCatalog.NameOf(detected)} — suas configurações foram mantidas " +
            "e os programas escolhidos foram convertidos para os equivalentes desta distribuição.");
    }

    void ShowIsoBanner(bool ok, string text)
    {
        IsoIdentityBanner.Visibility = Visibility.Visible;
        IsoIdentityIcon.Text = ok ? "" : "";   // check / alerta
        TxtIsoIdentity.Text = text;
        IsoIdentityBanner.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, ok ? "InfoBg" : "WarnBg");
        IsoIdentityBanner.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, ok ? "InfoBorder" : "WarnBorder");
        TxtIsoIdentity.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, ok ? "InfoText" : "WarnText");
        IsoIdentityIcon.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, ok ? "InfoText" : "WarnText");
        AppendLog(text);
    }

    // ------------------------------------------------------------------
    // Configurações específicas de Linux
    // ------------------------------------------------------------------
    void CollectLinuxConfig()
    {
        var lx = _config.Linux;

        lx.Delivery = RbDeliverySeed.IsChecked == true ? LinuxDeliveryMode.SeedIso : LinuxDeliveryMode.Repack;
        lx.Timezone = (CmbTimezone.SelectedItem as ComboBoxItem)?.Content as string
                      ?? CmbTimezone.Text.Trim();
        if (string.IsNullOrWhiteSpace(lx.Timezone)) lx.Timezone = "America/Sao_Paulo";

        lx.LocaleId = (CmbLinuxLocale.SelectedItem as ComboBoxItem)?.Tag as string ?? "pt_BR.UTF-8";

        var keyboard = ((CmbKeyboard.SelectedItem as ComboBoxItem)?.Tag as string ?? "br|abnt2").Split('|');
        lx.KeyboardLayout = keyboard[0];
        lx.KeyboardVariant = keyboard.Length > 1 ? keyboard[1] : "";

        lx.Desktop = Enum.TryParse<LinuxDesktop>((CmbDesktop.SelectedItem as ComboBoxItem)?.Tag as string, out var de)
            ? de : LinuxDesktop.Default;

        lx.FullName = TxtFullName.Text.Trim();
        lx.ExtraPackages = TxtExtraPackages.Text.Trim();

        lx.DiskMode = RbDiskCrypt.IsChecked == true ? LinuxDiskMode.EntireDiskEncrypted
            : RbDiskLvm.IsChecked == true ? LinuxDiskMode.EntireDiskLvm
            : LinuxDiskMode.EntireDisk;
        lx.DiskPassword = TxtDiskPassword.Text;
        lx.TargetDisk = TxtTargetDisk.Text.Trim();

        lx.UpdateDuringInstall = ChkLinuxUpdate.IsChecked == true;
        lx.ProprietaryDrivers = ChkLinuxDrivers.IsChecked == true;
        lx.EnableFlatpak = ChkLinuxFlatpak.IsChecked == true;
        lx.MinimalInstall = ChkLinuxMinimal.IsChecked == true;
        lx.InstallSshServer = ChkLinuxSsh.IsChecked == true;
        lx.AutoLogin = ChkLinuxAutoLogin.IsChecked == true;
        lx.DisableRootPassword = ChkLinuxNoRoot.IsChecked == true;
        lx.SshAuthorizedKey = TxtSshKey.Text.Trim();

        lx.RemoveDefaultGames = ChkLxDebloatGames.IsChecked == true;
        lx.DisableTelemetry = ChkLxDebloatTelemetry.IsChecked == true;
        lx.RemoveSnap = ChkLxDebloatSnap.IsChecked == true;
        lx.DisableMotdAds = ChkLxDebloatMotd.IsChecked == true;
    }

    /// <summary>
    /// Escreve num ComboBox editavel. Atribuir <c>Text</c> antes do template ser
    /// aplicado NAO gruda (a caixa de texto do template ainda nao existe) e o
    /// campo abria em branco; selecionar o item equivalente sobrevive. So quando
    /// o valor nao esta na lista e que resta o texto, ai adiado para depois do
    /// template.
    /// </summary>
    static void DefinirComboEditavel(ComboBox combo, string valor)
    {
        var item = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Content as string, valor, StringComparison.OrdinalIgnoreCase));

        if (item != null) { combo.SelectedItem = item; return; }

        combo.SelectedItem = null;
        combo.Text = valor;
        if (combo.IsLoaded) return;

        void Reaplicar(object s, RoutedEventArgs e)
        {
            combo.Loaded -= Reaplicar;
            combo.Text = valor;
        }
        combo.Loaded += Reaplicar;
    }

    void ApplyLinuxConfigToUi()
    {
        var lx = _config.Linux;

        RbDeliverySeed.IsChecked = lx.Delivery == LinuxDeliveryMode.SeedIso;
        RbDeliveryRepack.IsChecked = lx.Delivery != LinuxDeliveryMode.SeedIso;

        // Perfil antigo (ou salvo antes deste campo existir) trazia fuso vazio e o
        // combo abria em branco; o padrao vale tambem na volta.
        DefinirComboEditavel(CmbTimezone,
            string.IsNullOrWhiteSpace(lx.Timezone) ? "America/Sao_Paulo" : lx.Timezone);
        SelectByTag(CmbLinuxLocale, lx.LocaleId);
        SelectByTag(CmbKeyboard, $"{lx.KeyboardLayout}|{lx.KeyboardVariant}");
        SelectByTag(CmbDesktop, lx.Desktop.ToString());

        TxtFullName.Text = lx.FullName;
        TxtExtraPackages.Text = lx.ExtraPackages;

        RbDiskCrypt.IsChecked = lx.DiskMode == LinuxDiskMode.EntireDiskEncrypted;
        RbDiskLvm.IsChecked = lx.DiskMode == LinuxDiskMode.EntireDiskLvm;
        RbDiskSimple.IsChecked = lx.DiskMode == LinuxDiskMode.EntireDisk;
        TxtDiskPassword.Text = lx.DiskPassword;
        TxtTargetDisk.Text = lx.TargetDisk;
        LinuxDisk_Changed(this, new RoutedEventArgs());

        ChkLinuxUpdate.IsChecked = lx.UpdateDuringInstall;
        ChkLinuxDrivers.IsChecked = lx.ProprietaryDrivers;
        ChkLinuxDriversMirror.IsChecked = lx.ProprietaryDrivers;
        ChkLinuxFlatpak.IsChecked = lx.EnableFlatpak;
        ChkLinuxMinimal.IsChecked = lx.MinimalInstall;
        ChkLinuxSsh.IsChecked = lx.InstallSshServer;
        ChkLinuxAutoLogin.IsChecked = lx.AutoLogin;
        ChkLinuxNoRoot.IsChecked = lx.DisableRootPassword;
        TxtSshKey.Text = lx.SshAuthorizedKey;

        ChkLxDebloatGames.IsChecked = lx.RemoveDefaultGames;
        ChkLxDebloatTelemetry.IsChecked = lx.DisableTelemetry;
        ChkLxDebloatSnap.IsChecked = lx.RemoveSnap;
        ChkLxDebloatMotd.IsChecked = lx.DisableMotdAds;

        if (_config.IsLinux) UpdateDeliveryUi();
    }

    static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var obj in combo.Items)
            if (obj is ComboBoxItem item && (item.Tag as string) == tag) { combo.SelectedItem = item; return; }
    }
}
