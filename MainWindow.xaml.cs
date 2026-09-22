using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IsoForge.Core;
using IsoForge.Core.Linux;
using IsoForge.Models;
using Microsoft.Win32;

namespace IsoForge;

public partial class MainWindow : Window
{
    BuildConfig _config = new();
    readonly InstallerFetcher _fetcher = new();
    readonly ConcurrentDictionary<AppId, FetchResult> _fetched = new();
    readonly DellDriverCatalog _driverCatalog = new();
    readonly LenovoDriverCatalog _lenovoCatalog = new();
    readonly HpDriverCatalog _hpCatalog = new();
    readonly DellComponentCatalog _componentCatalog = new();
    readonly LenovoComponentCatalog _lenovoComponentCatalog = new();
    readonly HpComponentCatalog _hpComponentCatalog = new();
    List<DriverPackModel> _dellModels = new();
    List<DriverModelRef> _componentModels = new();
    readonly ObservableCollection<DriverCategoryVm> _driverCategories = new();
    readonly ObservableCollection<DriverItemVm> _individualDrivers = new();
    bool DriverIndividualMode => RbDrvIndividual?.IsChecked == true;
    // Fabricante ativo. Pack: Dell/Lenovo/HP. Individual (avulso): Dell e Lenovo.
    string DriverVendor => (CmbDriverVendor?.SelectedItem as ComboBoxItem)?.Content as string ?? "Dell";
    bool IsLenovo => DriverVendor == "Lenovo";
    bool IsHp => DriverVendor == "HP";
    bool IndividualSupported => true; // Dell, Lenovo e HP suportam avulso
    IDriverPackCatalog PackCatalog => IsHp ? _hpCatalog : IsLenovo ? _lenovoCatalog : _driverCatalog;
    IDriverComponentCatalog ComponentCatalog => IsHp ? _hpComponentCatalog : IsLenovo ? _lenovoComponentCatalog : _componentCatalog;
    CancellationTokenSource? _cts;
    bool _syncingCards;

    /// <param name="escolhido">
    /// Sistema a abrir direto. Passando <c>null</c> (o caso do app), a janela sobe
    /// com a camada de escolha do sistema por cima do shell.
    /// </param>
    public MainWindow(TargetOs? escolhido = null)
    {
        InitializeComponent();
        RunVersion.Text = $"v{Updater.CurrentVersion.ToString(3)}";

        // Carrega a configuração salva localmente. Assim os dados preenchidos sobrevivem a
        // atualizações e nada sensível fica no código.
        _config = SettingsStore.Load() ?? _config;

        // Sem escolha explícita, o shell parte do último sistema usado — a camada de
        // escolha decide por cima. Com escolha explícita, os aplicativos precisam ser
        // reconvertidos: senão um perfil salvo no Linux abriria no Windows ainda
        // mostrando "Evince (leitor de PDF)" na lista.
        // Reaberto como administrador so para gravar o pendrive: a pessoa ja estava no meio
        // de uma gravacao e quem reabriu a janela foi o programa, nao ela. Perguntar o
        // sistema de novo — e ainda por cima tocar a abertura — e fazer o trabalho dela
        // duas vezes por uma decisao que foi nossa.
        if (App.ModoPendrive) escolhido ??= _config.Os;

        var salvo = _config.Os;
        var os = escolhido ?? salvo;
        _config.Os = os;
        if (salvo != os)
            Loaded += async (_, __) =>
            {
                _config.Os = salvo;               // SwitchOsAsync parte do sistema anterior
                await SwitchOsAsync(os, "escolha na tela de abertura");
            };

        GridVpn.ItemsSource = _config.VpnTunnels;
        GridUnits.ItemsSource = _config.Units;
        LstDriverCategories.ItemsSource = _driverCategories;
        LstIndividualDrivers.ItemsSource = _individualDrivers;

        ApplyConfigToUi();
        ApplyOsToUi();
        RefreshProfiles();
        RefreshAppCards();
        UpdateUnitPreview();

        // Salva ao fechar — garante que nada preenchido se perca. Menos quando esta
        // instância está fechando para dar lugar a uma elevada: aí as duas escreveriam o
        // mesmo settings.dat ao mesmo tempo, e a gravação não é atômica — um arquivo
        // truncado faria a configuração inteira, senhas inclusive, voltar ao padrão.
        Closing += (_, __) =>
        {
            // Fechar com trabalho em andamento deixava o processo filho vivo: a janela
            // sumia e o robocopy continuava gravando no pendrive. Cancelar aqui faz o
            // registro do token matá-lo junto.
            if (_ocupado) { try { _cts?.Cancel(); } catch { } }
            if (App.EntregandoBastao) return;
            try { CollectConfig(); } catch { }
        };

        // Reaberto como administrador vindo da tela de pendrive: retoma de onde parou.
        // No ContentRendered, e não no Loaded: o diálogo é modal e precisa de uma janela
        // já pintada embaixo dele, senão nasce sobre um retângulo em branco.
        if (App.ModoPendrive)
            ContentRendered += async (_, __) =>
            {
                try
                {
                    if (App.IsoParaGravar != null)
                        await OfereceGravarPendriveAsync(App.IsoParaGravar, reaberto: true);
                    else
                        BuildUsb_Click(this, new RoutedEventArgs());
                }
                catch (Exception ex) { Caixa.Erro(this, "Não consegui retomar a gravação", ex.Message); }
            };

        // Reflete os apps escolhidos em outras telas.
        _config.Apps.CollectionChanged += (_, __) => Dispatcher.Invoke(() => { /* IMAGEM GOLDEN (DESATIVADA): UpdateGoldenSummary(); */ UpdateDynamicConfigCards(); });
        // IMAGEM GOLDEN (DESATIVADA): UpdateGoldenSummary();

        var bundled = Oscdimg.ExtractBundled();
        if (bundled != null)
        {
            TxtOscdimg.Text = bundled; // embutido no IsoForge; sem log (é interno)
        }
        else
        {
            var oscdimg = Oscdimg.LocateInstalled();
            if (oscdimg != null)
            {
                TxtOscdimg.Text = oscdimg;
                AppendLog($"oscdimg encontrado: {oscdimg}");
            }
            else
            {
                AppendLog("oscdimg.exe não encontrado (esta compilação veio sem o binário embutido).");
                AppendLog("Informe o caminho manualmente ou instale o Windows ADK.");
            }
        }
        AppendLog("");
        StartInstallerRefresh();

        StartBackdrop();

        // A area NAO cliente (titulo e botoes de janela) e desenhada pelo Windows e
        // ignora o tema do WPF: com o app no escuro ela continuava branca. So da
        // para pintar depois de a janela ter handle.
        SourceInitialized += (_, __) => ThemeService.ApplyTitleBar(this, _config.AppDarkTheme);

        // Sem sistema definido pelo chamador: pergunta, dentro desta mesma janela.
        if (escolhido == null) ShowPicker(primeira: true);

        // A abertura vem por último: ela precisa da tela de baixo já montada para
        // saber para onde voar. No modo pendrive ela não toca: a animação existe para
        // apresentar o programa a quem acabou de abri-lo, e aqui quem abriu fui eu.
        if (!App.ModoPendrive) ShowSplash();

        // Tela baixa: o console vazio custava mais do que informava. Quem decide
        // e o primeiro layout do Shell, nao o Loaded da janela: assim tambem vale
        // fora de uma janela exibida (render offscreen dos testes de layout).
        // O limiar era 880, calibrado para "janela de 900 menos a barra de titulo" e nao
        // para tela de verdade: ele pegava 1366x768, 1600x900, 1440x900 e ate 1920x1080
        // a 125% de escala (1536x864 logicos), que e o padrao recomendado em notebook
        // FHD. Em 768 sobram ~689 px uteis; com 140 para o console restam ~549 para o
        // formulario, que rola.
        SizeChangedEventHandler? primeiroLayout = null;
        primeiroLayout = (_, e) =>
        {
            Shell.SizeChanged -= primeiroLayout;
            if (e.NewSize.Height < 700) SetLogCollapsed(true);
        };
        Shell.SizeChanged += primeiroLayout;

        _ = CheckForUpdateAsync();
    }

    // Abre a página do projeto no GitHub no navegador padrão.
    void Github_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { }
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // Auto-atualização
    // ------------------------------------------------------------------
    async Task CheckForUpdateAsync()
    {
        try
        {
            var info = await Updater.CheckAsync();
            if (info == null || Updater.IsSkipped(info.Version)) return;

            var dlg = new UpdateWindow(info.Version, Updater.CurrentVersion, info.Notes) { Owner = this };
            dlg.ShowDialog();
            if (dlg.Choice == UpdateChoice.Skip) { Updater.SkipVersion(info.Version); return; }
            if (dlg.Choice != UpdateChoice.Update) return;

            var pct = new Progress<double>(p => Dispatcher.Invoke(() =>
            {
                Status($"Baixando atualização: {p:0}%");
                DownloadBar.Visibility = Visibility.Visible;
                DownloadBar.IsIndeterminate = false;
                DownloadBar.Value = p;
            }));
            var path = await Updater.DownloadAsync(info, pct);
            CollectConfig();
            Updater.RunInstaller(path);
        }
        catch (Exception ex)
        {
            // Limite de requisicoes do GitHub nao e problema do usuario: nao vale
            // um erro cru no console.
            AppendLog(ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                ? "Verificação de atualização adiada (limite de consultas ao GitHub)."
                : $"Verificação de atualização: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Busca automática dos instaladores mais recentes (ao abrir)
    // ------------------------------------------------------------------
    void StartInstallerRefresh()
    {
        // No Linux os programas vêm dos repositórios da distro, instalados no 1º boot:
        // não há instalador para baixar aqui.
        if (_config.IsLinux)
        {
            Status("Pronto");
            return;
        }
        Status("Buscando instaladores mais recentes...");
        DownloadBar.Visibility = Visibility.Visible;
        DownloadBar.IsIndeterminate = true;
        var progress = new Progress<string>(AppendLog);
        _ = Task.Run(async () =>
        {
            var ids = new[] { AppId.SevenZip, AppId.AnyDesk, AppId.OfficeOdt };

            // EM PARALELO, nao em fila. Eram tres consultas HTTP em serie (cada uma
            // seguindo redirecionamentos) so para descobrir a versao mais recente, e
            // o usuario esperava a soma delas com "Buscando instaladores..." na tela.
            //
            // E seguro: o InstallerFetcher usa um HttpClient ESTATICO (a forma certa
            // de compartilhar), _fetched e um ConcurrentDictionary, e cada app grava
            // na PROPRIA pasta (FolderFor(id)) — nao ha caminho compartilhado entre
            // ids diferentes.
            await Task.WhenAll(ids.Select(async id =>
            {
                try
                {
                    var r = await _fetcher.EnsureAsync(id, progress, CancellationToken.None);
                    if (r.LocalPath != null) _fetched[id] = r;
                }
                catch (Exception ex)
                {
                    // Uma falha de rede num app nao pode derrubar os outros dois.
                    // Progress<T> implementa Report so pela interface.
                    ((IProgress<string>)progress).Report($"Nao foi possivel consultar {id}: {ex.Message}");
                }
            }));

            // O resumo sai da ordem de IDS, nao de _fetched.Values: a ordem de um
            // ConcurrentDictionary nao e garantida, e agora que as tarefas terminam
            // fora de ordem isso viraria uma lista embaralhada a cada abertura.
            var resumo = string.Join(" · ", ids
                .Where(_fetched.ContainsKey)
                .Select(id => _fetched[id])
                .Select(v => v.Version == "mais recente" ? v.Name : $"{v.Name} {v.Version}"));
            Dispatcher.Invoke(() =>
            {
                Status(string.IsNullOrEmpty(resumo) ? "Instaladores: verifique a conexão" : $"Instaladores prontos: {resumo}");
                HideDownloadBar();
            });
        });
    }

    // Progresso em porcentagem no painel de status (texto + barra bem visíveis).
    IProgress<double> PercentTo(string label) =>
        new Progress<double>(pct =>
        {
            Status($"{label}: {pct:0}%");
            DownloadBar.Visibility = Visibility.Visible;
            DownloadBar.IsIndeterminate = false;
            DownloadBar.Value = pct;
            if (pct >= 99.5) HideDownloadBar();
        });

    void HideDownloadBar()
    {
        DownloadBar.IsIndeterminate = false;
        DownloadBar.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // Aplicativos: cards selecionáveis + config dinâmica
    // ------------------------------------------------------------------
    void RefreshAppCards()
    {
        _syncingCards = true;
        CardOffice.IsChecked = HasApp(AppId.OfficeOdt);
        CardAnyDesk.IsChecked = HasApp(AppId.AnyDesk);
        CardSevenZip.IsChecked = HasApp(AppId.SevenZip);
        CardForti.IsChecked = HasApp(AppId.FortiClient);
        CardAdobe.IsChecked = HasApp(AppId.AdobeReader);
        CardChrome.IsChecked = HasApp(AppId.Chrome);
        CardFirefox.IsChecked = HasApp(AppId.Firefox);
        CardNotepad.IsChecked = HasApp(AppId.NotepadPlus);
        CardVcRedist.IsChecked = HasApp(AppId.VcRedist);
        _syncingCards = false;
        AppsChips.ItemsSource = _config.Apps;
        UpdateDynamicConfigCards();
    }

    void UpdateDynamicConfigCards()
    {
        if (OfficeConfigCard == null) return;
        bool office = HasApp(AppId.OfficeOdt);
        bool forti = HasApp(AppId.FortiClient);
        OfficeConfigCard.Visibility = office ? Visibility.Visible : Visibility.Collapsed;
        FortiConfigCard.Visibility = forti ? Visibility.Visible : Visibility.Collapsed;
        EmptyAppsHint.Visibility = _config.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void CardOffice_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingCards) return;
        if (CardOffice.IsChecked == true)
        {
            var ok = _config.IsLinux ? AddLinuxApp(AppId.OfficeOdt) : await AddOfficeAsync();
            if (!ok) { _syncingCards = true; CardOffice.IsChecked = false; _syncingCards = false; }
        }
        else
        {
            RemoveApp(AppId.OfficeOdt);
        }
        UpdateDynamicConfigCards();
    }

    async void CardAnyDesk_Click(object sender, RoutedEventArgs e) => await ToggleAppCard(CardAnyDesk, AppId.AnyDesk, "AnyDesk");
    async void CardSevenZip_Click(object sender, RoutedEventArgs e) => await ToggleAppCard(CardSevenZip, AppId.SevenZip, "7-Zip");
    async void CardAdobe_Click(object sender, RoutedEventArgs e) => await ToggleAppCard(CardAdobe, AppId.AdobeReader, "Adobe Acrobat Reader");
    // Ao selecionar o FortiClient, abre uma tela perguntando a versão (7.4.1 offline × mais recente oficial).
    async void CardForti_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingCards) return;
        if (CardForti.IsChecked == true)
        {
            bool ok;
            if (_config.IsLinux)
            {
                // No Linux a Fortinet publica um único pacote por distro: não há o que escolher.
                ok = AddLinuxApp(AppId.FortiClient);
            }
            else
            {
                var dlg = new FortiVersionWindow { Owner = this };
                if (dlg.ShowDialog() != true)
                {
                    _syncingCards = true; CardForti.IsChecked = false; _syncingCards = false;
                    return;
                }
                _config.FortiClientLatest = dlg.Latest;
                ok = await AddAutoAsync(dlg.Latest ? AppId.FortiClientLatest : AppId.FortiClient);
            }
            if (!ok) { _syncingCards = true; CardForti.IsChecked = false; _syncingCards = false; }
        }
        else
        {
            RemoveApp(AppId.FortiClient);
        }
        UpdateDynamicConfigCards();
    }
    async void CardChrome_Click(object sender, RoutedEventArgs e) => await ToggleAppCard(CardChrome, AppId.Chrome, "Google Chrome");
    async void CardFirefox_Click(object sender, RoutedEventArgs e) => await ToggleAppCard(CardFirefox, AppId.Firefox, "Mozilla Firefox");
    async void CardNotepad_Click(object sender, RoutedEventArgs e) => await ToggleAppCard(CardNotepad, AppId.NotepadPlus, "Notepad++");
    async void CardVcRedist_Click(object sender, RoutedEventArgs e) => await ToggleAppCard(CardVcRedist, AppId.VcRedist, "Visual C++ 2015-2022 (x64)");

    async Task ToggleAppCard(ToggleButton card, AppId id, string name)
    {
        if (_syncingCards) return;
        if (card.IsChecked == true)
        {
            var ok = _config.IsLinux ? AddLinuxApp(id) : await AddAutoAsync(id);
            if (!ok) { _syncingCards = true; card.IsChecked = false; _syncingCards = false; }
        }
        else
        {
            RemoveApp(id);
        }
        UpdateDynamicConfigCards();
    }

    async Task<bool> AddOfficeAsync()
    {
        if (_config.Apps.Any(a => a.Kind == AppKind.Office)) return true;
        SetBusy(true);
        var progress = new Progress<string>(AppendLog);
        var percent = PercentTo("Baixando Office Deployment Tool");
        try
        {
            var odt = await _fetcher.EnsureAsync(AppId.OfficeOdt, progress, CancellationToken.None, percent);
            if (odt.LocalPath == null || !File.Exists(odt.LocalPath))
            {
                // MOSTRA O MOTIVO REAL. A mensagem antiga era sempre "Verifique a
                // internet", e o FetchResult.Error com a causa verdadeira era descartado.
                // Quando a Microsoft trocou o formato do pacote do ODT, quem usava viu
                // "verifique a internet" com a internet perfeita — e não havia como
                // chegar na causa a partir da tela.
                var motivo = string.IsNullOrWhiteSpace(odt.Error)
                    ? "Não consegui obter o Office Deployment Tool. Verifique a internet."
                    : "Não consegui obter o Office Deployment Tool:\n\n" + odt.Error;
                AppendLog("Office 365: " + (odt.Error ?? "falha sem detalhe ao obter o ODT."));
                Caixa.Avisar(this, motivo);
                return false;
            }
            _fetched[AppId.OfficeOdt] = odt;
            _config.Apps.Add(new AppEntry
            {
                Name = odt.Name, InstallerPath = odt.LocalPath, SilentArgs = "",
                Kind = AppKind.Office, CatalogId = nameof(AppId.OfficeOdt)
            });
            _config.OfficeOffline = false;
            _syncingCards = true;
            RbOfficeOnline.IsChecked = true;
            _syncingCards = false;
            AppendLog("Office 365 adicionado (online por padrão). Escolha Online/Offline e o idioma nas opções abaixo.");
            return true;
        }
        catch (Exception ex) { AppendLog($"ERRO: {ex.Message}"); return false; }
        finally { SetBusy(false); }
    }

    async Task<bool> AddAutoAsync(AppId id)
    {
        var known = _fetched.TryGetValue(id, out var cached) ? cached : null;
        string? motivo = null;
        if (known == null)
        {
            SetBusy(true);
            var progress = new Progress<string>(AppendLog);
            var percent = PercentTo("Baixando");
            // Sem catch, uma falha de rede aqui subia por SwitchOsAsync ate uma lambda
            // async void e derrubava o processo. Falhar em baixar um instalador nao pode
            // matar o app — o usuario ve o aviso logo abaixo e escolhe o que fazer.
            try { known = await _fetcher.EnsureAsync(id, progress, CancellationToken.None, percent); }
            catch (Exception ex) { AppendLog($"Falha ao obter {id}: {ex.Message}"); known = null; motivo = ex.Message; }
            finally { SetBusy(false); }
            if (known?.LocalPath != null) _fetched[id] = known;
            Status(known?.LocalPath != null ? $"{known.Name} pronto" : TxtHeaderStatus.Text);
        }

        if (known?.LocalPath == null || !File.Exists(known.LocalPath))
        {
            // MOSTRA O MOTIVO REAL — o mesmo conserto já feito no AddOfficeAsync, que aqui
            // faltava para os outros nove instaladores. O FetchResult traz a causa em
            // .Error (InstallerFetcher.EnsureAsync a guarda ali) e a tela a jogava fora,
            // trocando-a por "verifique a internet". Isso é mentira em todos os casos
            // reais: 404 do CDN, formato da página da fonte mudou, 403 de limite da API do
            // GitHub, proxy, disco cheio. O log dizia a verdade e a caixa de diálogo — a
            // única coisa que o usuário vê — o desmentia.
            motivo ??= known?.Error;
            if (motivo == null && known?.LocalPath != null)
                motivo = $"o download terminou mas o arquivo não está em {known.LocalPath}.";
            var texto = string.IsNullOrWhiteSpace(motivo)
                ? "Não consegui baixar o instalador automaticamente. Verifique a internet e tente novamente, ou use \"+ Adicionar outro\"."
                : $"Não consegui obter {known?.Name ?? id.ToString()}:\n\n{motivo}";
            AppendLog($"{known?.Name ?? id.ToString()}: {motivo ?? "falha sem detalhe."}");
            Caixa.Avisar(this, texto);
            return false;
        }
        if (HasApp(id)) return true;
        _config.Apps.Add(new AppEntry
        {
            Name = known.Name,
            InstallerPath = known.LocalPath,
            SilentArgs = known.SilentArgs,
            Kind = known.IsOffice ? AppKind.Office : AppKind.Generic,
            RequiresInternet = known.RequiresInternet,
            CatalogId = Canonical(id).ToString()
        });
        AppendLog($"{known.Name} adicionado (versão {known.Version}).");
        return true;
    }

    void RemoveAppChip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is AppEntry app)
        {
            _config.Apps.Remove(app);
            RefreshAppCards();
        }
    }

    void AddCustomApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Instaladores|*.exe;*.msi|Todos os arquivos|*.*",
            Title = "Selecione o instalador do aplicativo"
        };
        if (dlg.ShowDialog() != true) return;

        var isMsi = Path.GetExtension(dlg.FileName).Equals(".msi", StringComparison.OrdinalIgnoreCase);
        _config.Apps.Add(new AppEntry
        {
            Name = Path.GetFileNameWithoutExtension(dlg.FileName),
            InstallerPath = dlg.FileName,
            SilentArgs = isMsi ? "/qn /norestart" : "/S",
            Kind = AppKind.Generic
        });
    }

    // Config dinâmica do Office
    void OfficeMode_Changed(object sender, RoutedEventArgs e)
    {
        if (PanelOfficeSource == null || RbOfficeOffline == null) return;
        bool offline = RbOfficeOffline.IsChecked == true;
        _config.OfficeOffline = offline;
        PanelOfficeSource.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
        // IMAGEM GOLDEN (DESATIVADA): UpdateGoldenSummary();
        if (offline && string.IsNullOrWhiteSpace(TxtOfficeSource.Text))
            AppendLog("Office offline selecionado. Clique em 'Baixar Office...' para embutir o Office na ISO (~3,5 GB).");
    }

    void AutoWifi_Changed(object sender, RoutedEventArgs e)
    {
        if (PanelWifi == null) return;
        PanelWifi.IsEnabled = ChkAutoWifi.IsChecked == true;
    }

    // Tema claro/escuro da interface do IsoForge.
    void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _config.AppDarkTheme = !_config.AppDarkTheme;
        ThemeService.Apply(_config.AppDarkTheme);
        ThemeService.ApplyTitleBar(this, _config.AppDarkTheme);
        // Depois do tema, sempre: o ThemeService nao escreve as chaves OsAccent*,
        // e sem esta linha alternar o tema apagaria a marca do sistema.
        OsAccentService.Apply(_config.Os, _config.AppDarkTheme);
        UpdateThemeButton();
    }

    void UpdateThemeButton()
    {
        if (BtnTheme == null) return;
        // Mostra o destino do clique: sol (☀) = mudar p/ claro; lua (☾) = mudar p/ escuro.
        BtnTheme.Content = _config.AppDarkTheme ? "☀" : "☾";
        BtnTheme.ToolTip = _config.AppDarkTheme ? "Mudar para tema claro" : "Mudar para tema escuro";
    }

    // ------------------------------------------------------------------
    // Drivers do fabricante (injeção por modelo) — Dell
    // ------------------------------------------------------------------
    bool _drvSelecting;
    bool _driverModelsLoading;
    string _pendingModelLabel = "";

    // Carrega os modelos automaticamente ao entrar na aba Drivers.
    /// <summary>
    /// O título da topbar acompanha a direção da navegação. Ele é o único elemento
    /// da troca de aba que NÃO é recriado — o binding só troca o texto —, então sem
    /// este gesto é a única coisa da tela que muda de valor sem se mexer.
    /// </summary>
    void AnimarTituloDaAba(int direcao)
    {
        if (TopbarTitle == null) return;
        if (TopbarTitle.RenderTransform is not TranslateTransform t)
            TopbarTitle.RenderTransform = t = new TranslateTransform();

        t.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(direcao * 10, 0, TimeSpan.FromMilliseconds(200))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        TopbarTitle.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(180)));
    }

    void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl) return; // ignora SelectionChanged de listas/combos internos

        // DIREÇÃO DA NAVEGAÇÃO. A barra lateral é vertical: ir para uma aba mais
        // ABAIXO empurra a página para cima (o conteúdo novo sobe de baixo) e o
        // contrário para cima. Sem eixo, toda troca de aba é um corte seco.
        // Escrito ANTES do Loaded da página nova, que roda neste mesmo tique.
        var anterior = e.RemovedItems.Count > 0 ? MainTabs.Items.IndexOf(e.RemovedItems[0]) : -1;
        Ui.CardEntrance.Direction = anterior < 0 || MainTabs.SelectedIndex >= anterior ? 1 : -1;
        AnimarTituloDaAba(Ui.CardEntrance.Direction);

        if ((MainTabs.SelectedItem as TabItem)?.Header as string != "Drivers") return;

        // O catálogo de drivers por modelo é exclusivo do Windows: no Linux os drivers vêm no kernel.
        if (_config.IsLinux)
            ChkLinuxDriversMirror.IsChecked = ChkLinuxDrivers.IsChecked; // reflete a opção da aba Sistema
        else
            _ = LoadDriverModelsAsync(force: false);
    }

    void ShowModelList()
    {
        DriverPickPanel.Visibility = Visibility.Collapsed;
        LstDriverModels.Visibility = Visibility.Visible;
        PanelDriverSearch.Visibility = Visibility.Visible;
    }

    // Troca de fabricante (Dell/Lenovo/HP). Todos têm pack e individual.
    void DriverVendor_Changed(object sender, SelectionChangedEventArgs e)
    {
        // DriverComponentsCard é criado depois do ComboBox no XAML: se ainda é null, o parse não terminou.
        if (DriverComponentsCard == null) return;
        RbDrvIndividual.IsEnabled = true;
        RbDrvIndividual.ToolTip = null;
        _dellModels = new(); _componentModels = new(); _individualDrivers.Clear();
        _drvSelecting = true; LstDriverModels.ItemsSource = null; _drvSelecting = false;
        ShowModelList();
        TxtDriverSearch.IsEnabled = false;
        BtnDriverDownload.IsEnabled = false;
        DriverComponentsCard.Visibility = Visibility.Collapsed;
        _ = LoadDriverModelsAsync(force: false);
    }

    void DriverMode_Changed(object sender, RoutedEventArgs e)
    {
        if (TxtDriverModeHint == null) return;
        bool indiv = DriverIndividualMode;
        TxtDriverModeHint.Text = indiv
            ? "Drivers individuais: escolha o modelo e marque só os drivers que quer (baixa MBs). Extrai via o instalador assinado do fabricante (pede UAC 1x)."
            : "Pack completo: baixa tudo do modelo de uma vez (GBs), sem executar nada. Garantido.";
        BtnDriverDownload.Content = indiv ? "Baixar selecionados" : "Baixar pack deste modelo";
        // Troca de modo: volta a escolher modelo e limpa (os catálogos são diferentes).
        _dellModels = new(); _componentModels = new(); _individualDrivers.Clear();
        _drvSelecting = true; LstDriverModels.ItemsSource = null; _drvSelecting = false;
        ShowModelList();
        TxtDriverSearch.IsEnabled = false;
        BtnDriverDownload.IsEnabled = false;
        DriverComponentsCard.Visibility = Visibility.Collapsed;
        _ = LoadDriverModelsAsync(force: false); // carrega os modelos do novo modo automaticamente
    }

    // Botão "Recarregar" força a releitura do catálogo.
    async void LoadDrivers_Click(object sender, RoutedEventArgs e) => await LoadDriverModelsAsync(force: true);

    // Carrega os modelos do modo atual. force=true releitura; force=false só se ainda não tem.
    async Task LoadDriverModelsAsync(bool force)
    {
        if (_driverModelsLoading) return;
        bool have = DriverIndividualMode ? _componentModels.Count > 0 : _dellModels.Count > 0;
        if (have && !force)
        {
            // já carregado: só garante a lista utilizável (sem mexer se estiver escolhendo drivers).
            if (TxtDriverSearch != null && DriverPickPanel.Visibility != Visibility.Visible)
                TxtDriverSearch.IsEnabled = true;
            return;
        }
        if (force) { _dellModels = new(); _componentModels = new(); }

        _driverModelsLoading = true;
        SetBusy(true);
        var progress = new Progress<string>(AppendLog);
        try
        {
            if (DriverIndividualMode)
            {
                var comp = ComponentCatalog; // avalia na thread da UI (lê o ComboBox)
                var vendor = IsLenovo ? "Lenovo" : "Dell";
                await comp.EnsureLoadedAsync(progress, CancellationToken.None);
                _componentModels = comp.Models();
                AppendLog($"{_componentModels.Count} modelos {vendor} (Windows 11) com drivers individuais.");
            }
            else
            {
                var vendor = DriverVendor;
                var catalog = PackCatalog; // avalia na thread da UI (lê o ComboBox)
                AppendLog($"Carregando catálogo de drivers da {vendor}...");
                _dellModels = await Task.Run(() => catalog.FetchModelsAsync(progress, CancellationToken.None));
                AppendLog($"{_dellModels.Count} modelos {vendor} (Windows 11 x64) carregados.");
            }
            ShowModelList();
            TxtDriverSearch.IsEnabled = true;
            BtnDriverDownload.IsEnabled = false; // habilita ao selecionar um modelo
            FilterDriverModels();
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO ao carregar catálogo de drivers: {ex.Message}");
        }
        finally { SetBusy(false); _driverModelsLoading = false; }
    }

    void DriverSearch_Changed(object sender, TextChangedEventArgs e) => FilterDriverModels();

    void FilterDriverModels()
    {
        if (LstDriverModels == null) return;
        _drvSelecting = true;
        var q = TxtDriverSearch.Text?.Trim() ?? "";
        if (DriverIndividualMode)
        {
            IEnumerable<DriverModelRef> items = _componentModels;
            if (q.Length > 0) items = _componentModels.Where(m => m.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
            LstDriverModels.DisplayMemberPath = "Name";
            LstDriverModels.ItemsSource = items.Take(300).ToList();
        }
        else
        {
            IEnumerable<DriverPackModel> items = _dellModels;
            if (q.Length > 0) items = _dellModels.Where(m => m.Label.Contains(q, StringComparison.OrdinalIgnoreCase));
            LstDriverModels.DisplayMemberPath = "Label";
            LstDriverModels.ItemsSource = items.Take(300).ToList();
        }
        _drvSelecting = false;
        BtnDriverDownload.IsEnabled = false; // refiltrou -> sem seleção
    }

    // Clicar num modelo: pack só habilita o botão; individual lista os drivers NA MESMA caixa.
    async void DriverModel_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_drvSelecting || LstDriverModels.SelectedItem == null) return;

        if (!DriverIndividualMode)
        {
            BtnDriverDownload.IsEnabled = LstDriverModels.SelectedItem is DriverPackModel;
            return;
        }

        if (LstDriverModels.SelectedItem is not DriverModelRef m) return;
        var comp = ComponentCatalog; // avalia na thread da UI (lê o ComboBox)
        SetBusy(true);
        var progress = new Progress<string>(AppendLog);
        List<DriverComponent> drivers;
        try { drivers = await comp.DriversForModelAsync(m, progress, CancellationToken.None); }
        catch (Exception ex) { AppendLog($"ERRO ao listar drivers: {ex.Message}"); SetBusy(false); return; }
        SetBusy(false);

        _individualDrivers.Clear();
        foreach (var d in drivers)
            _individualDrivers.Add(new DriverItemVm { Name = d.Name, Detail = $"  [{d.Category} · {d.SizeText}]", Include = false, Driver = d });
        _pendingModelLabel = m.Name;
        if (_individualDrivers.Count == 0)
        {
            Caixa.Informar(this, $"Não achei drivers individuais para {m.Name}. Tente o modo 'Pack completo'.");
            return;
        }
        TxtPickTitle.Text = $"{m.Name} — marque os drivers:";
        LstDriverModels.Visibility = Visibility.Collapsed;
        PanelDriverSearch.Visibility = Visibility.Collapsed;
        DriverPickPanel.Visibility = Visibility.Visible;
        BtnDriverDownload.IsEnabled = true;
    }

    void DriverBack_Click(object sender, RoutedEventArgs e)
    {
        _individualDrivers.Clear();
        _drvSelecting = true; LstDriverModels.SelectedItem = null; _drvSelecting = false;
        ShowModelList();
        BtnDriverDownload.IsEnabled = false;
    }

    void IndivSelectAll_Click(object sender, RoutedEventArgs e) => SetAllIndividual(true);
    void IndivSelectNone_Click(object sender, RoutedEventArgs e) => SetAllIndividual(false);
    void SetAllIndividual(bool include)
    {
        var snap = _individualDrivers.ToList();
        _individualDrivers.Clear();
        foreach (var d in snap) { d.Include = include; _individualDrivers.Add(d); }
    }

    // CTA: pack baixa o pack inteiro; individual baixa só os drivers marcados.
    async void DownloadDriver_Click(object sender, RoutedEventArgs e)
    {
        if (DriverIndividualMode)
        {
            var chosen = _individualDrivers.Where(d => d.Include).Select(d => d.Driver).ToList();
            if (chosen.Count == 0)
            {
                Caixa.Informar(this, "Marque ao menos um driver.");
                return;
            }
            SetBusy(true);
            var prog = new Progress<string>(AppendLog);
            var pc = PercentTo("Baixando drivers selecionados");
            try
            {
                var comp = ComponentCatalog; // avalia na thread da UI (lê o ComboBox)
                var folder = await comp.DownloadAndExtractAsync(chosen, _pendingModelLabel, prog, pc, CancellationToken.None);
                _config.DriverPackPath = folder;
                _config.DriverModelName = $"{_pendingModelLabel} ({chosen.Count} driver(s))";
                _config.DriverExcludedCategories = new();
                TxtDriverStatus.Text = $"Drivers prontos: {_config.DriverModelName}. Serão injetados na ISO.";
                AppendLog($"Drivers individuais prontos em: {folder}");
            }
            catch (Exception ex)
            {
                AppendLog($"ERRO ao baixar drivers: {ex.Message}");
                Caixa.Avisar(this, $"Falha ao baixar/extrair os drivers.\n\n{ex.Message}");
            }
            finally { SetBusy(false); }
            return;
        }

        if (LstDriverModels.SelectedItem is not DriverPackModel model)
        {
            Caixa.Informar(this, "Selecione um modelo na lista primeiro.");
            return;
        }
        SetBusy(true);
        var progress = new Progress<string>(AppendLog);
        var percent = PercentTo($"Baixando driver {model.Label}");
        try
        {
            var catalog = PackCatalog; // avalia na thread da UI (lê o ComboBox)
            var folder = await Task.Run(() => catalog.DownloadAndExtractAsync(model, progress, percent, CancellationToken.None));
            _config.DriverPackPath = folder;
            _config.DriverModelName = model.Label;
            _config.DriverExcludedCategories = new();
            TxtDriverStatus.Text = $"Driver pronto: {model.Label}. Escolha os componentes abaixo.";
            AppendLog($"Driver do modelo {model.Label} baixado e extraído em: {folder}");
            await RefreshDriverCategoriesAsync(null);
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO ao baixar driver: {ex.Message}");
            Caixa.Avisar(this, $"Falha ao baixar/extrair o driver.\n\n{ex.Message}");
        }
        finally { SetBusy(false); }
    }

    void ClearDriver_Click(object sender, RoutedEventArgs e)
    {
        _config.DriverPackPath = "";
        _config.DriverModelName = "";
        _config.DriverExcludedCategories = new();
        _drvSelecting = true; LstDriverModels.SelectedItem = null; _drvSelecting = false;
        _driverCategories.Clear();
        _individualDrivers.Clear();
        DriverComponentsCard.Visibility = Visibility.Collapsed;
        ShowModelList();
        BtnDriverDownload.IsEnabled = false;
        TxtDriverStatus.Text = "Nenhum driver selecionado.";
    }

    // Lê as categorias (Rede, Vídeo…) do pack extraído e mostra os checkboxes.
    async Task RefreshDriverCategoriesAsync(ISet<string>? excluded)
    {
        _driverCategories.Clear();
        if (string.IsNullOrWhiteSpace(_config.DriverPackPath) || !Directory.Exists(_config.DriverPackPath))
        {
            DriverComponentsCard.Visibility = Visibility.Collapsed;
            return;
        }
        var path = _config.DriverPackPath;
        var cats = await Task.Run(() => DriverInfScanner.Scan(path));
        foreach (var c in cats)
            _driverCategories.Add(new DriverCategoryVm
            {
                Name = c.Name,
                Detail = $"  ({c.Count}) — {c.SizeText}",
                Include = excluded == null || !excluded.Contains(c.Name)
            });
        DriverComponentsCard.Visibility = cats.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void DriverSelectAll_Click(object sender, RoutedEventArgs e) => SetAllDriverCategories(true);
    void DriverSelectNone_Click(object sender, RoutedEventArgs e) => SetAllDriverCategories(false);

    void SetAllDriverCategories(bool include)
    {
        // Recria a coleção para o ItemsControl refletir a mudança (VM sem INotifyPropertyChanged).
        var snapshot = _driverCategories.ToList();
        _driverCategories.Clear();
        foreach (var c in snapshot) { c.Include = include; _driverCategories.Add(c); }
    }

    void OfficeLang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CmbOfficeLang?.SelectedItem is not ComboBoxItem it) return;
        var lang = (it.Tag as string) ?? "pt-br";
        _config.OfficeLanguage = lang;
        _config.OfficeConfigXml = BuildConfig.BuildOfficeConfig(lang);
    }

    // ------------------------------------------------------------------
    // Navegação de arquivos
    // ------------------------------------------------------------------
    async void BrowseSourceIso_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Imagem ISO|*.iso",
            Title = $"Selecione a ISO do {OsCatalog.NameOf(_config.Os)}"
        };
        if (dlg.ShowDialog() != true) return;
        TxtSourceIso.Text = dlg.FileName;
        if (string.IsNullOrWhiteSpace(TxtOutputIso.Text))
            TxtOutputIso.Text = SuggestedOutputPath(dlg.FileName);

        // Lê o conteúdo da ISO e, se ela for de outro sistema, corrige a escolha sozinho.
        await IdentifyIsoAndFixOsAsync(dlg.FileName);
    }

    void BrowseOutputIso_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Imagem ISO|*.iso", Title = "Salvar ISO personalizada como", FileName = "Win11_Personalizada.iso" };
        if (dlg.ShowDialog() == true) TxtOutputIso.Text = dlg.FileName;
    }

    void BrowseOscdimg_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "oscdimg.exe|oscdimg.exe", Title = "Localize o oscdimg.exe (Windows ADK)" };
        if (dlg.ShowDialog() == true) TxtOscdimg.Text = dlg.FileName;
    }

    void BrowseCustomUnattend_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "autounattend.xml|*.xml", Title = "Selecione seu autounattend.xml" };
        if (dlg.ShowDialog() == true)
        {
            TxtCustomUnattend.Text = dlg.FileName;
            RbCustomUnattend.IsChecked = true;
        }
    }

    void BrowsePostScript_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Scripts|*.ps1;*.cmd;*.bat|Todos os arquivos|*.*",
            Title = "Selecione o script executado no primeiro logon"
        };
        if (dlg.ShowDialog() == true) TxtPostScript.Text = dlg.FileName;
    }

    void ClearPostScript_Click(object sender, RoutedEventArgs e) => TxtPostScript.Text = "";

    void BrowseWallpaper_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Imagens|*.jpg;*.jpeg;*.png;*.bmp", Title = "Selecione o papel de parede padrão" };
        if (dlg.ShowDialog() == true) TxtWallpaper.Text = dlg.FileName;
    }

    void ClearWallpaper_Click(object sender, RoutedEventArgs e) => TxtWallpaper.Text = "";

    void BrowseLockScreen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Imagens|*.jpg;*.jpeg;*.png;*.bmp", Title = "Selecione a imagem da tela de bloqueio" };
        if (dlg.ShowDialog() == true) TxtLockScreen.Text = dlg.FileName;
    }

    void ClearLockScreen_Click(object sender, RoutedEventArgs e) => TxtLockScreen.Text = "";

    void RemoveVpn_Click(object sender, RoutedEventArgs e)
    {
        if (GridVpn.SelectedItem is VpnTunnel t) _config.VpnTunnels.Remove(t);
    }

    void BrowseFortiReg_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Registro exportado|*.reg", Title = "Selecione o .reg exportado do FortiClient" };
        if (dlg.ShowDialog() == true) TxtFortiReg.Text = dlg.FileName;
    }

    void ClearFortiReg_Click(object sender, RoutedEventArgs e) => TxtFortiReg.Text = "";

    void Xauth_Changed(object sender, RoutedEventArgs e)
    {
        if (PanelXauthCreds == null) return;
        PanelXauthCreds.IsEnabled = RbXauthSave.IsChecked == true;
    }

    void CaptureFortiReg_Click(object sender, RoutedEventArgs e)
    {
        const string key = @"HKLM\SOFTWARE\Fortinet\FortiClient";
        var outFile = Path.Combine(_fetcher.BaseFolder, "FortiClient-export.reg");
        try
        {
            Directory.CreateDirectory(_fetcher.BaseFolder);
            var psi = new System.Diagnostics.ProcessStartInfo("reg.exe", $"export \"{key}\" \"{outFile}\" /y")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || !File.Exists(outFile))
            {
                Caixa.Avisar(this, $"Não encontrei o FortiClient instalado/configurado neste computador (chave {key}).\n\n" +
                    "Instale o FortiClient aqui, configure os túneis uma vez e clique novamente — ou use \"Procurar...\" para apontar um .reg exportado em outra máquina.");
                return;
            }
            TxtFortiReg.Text = outFile;
            AppendLog($"Config do FortiClient capturada deste PC: {outFile}");
            Caixa.Informar(this, "Configuração do FortiClient capturada deste computador (inclui gateway + PSK cifrada).\n\n" +
                "Ela será importada exatamente como está no 1º logon da ISO — os túneis vão preencher completos (nome, gateway e senha).");
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO ao capturar config do FortiClient: {ex.Message}");
            Caixa.Erro(this, ex.Message);
        }
    }

    // ---------------------------------------------------------------- apps de fábrica
    // A lista é montada no código porque o catálogo é dado do programa (Core/BloatCatalog),
    // e porque agrupar por categoria na tela pede um CollectionViewSource.
    readonly List<Ui.BloatItemVm> _bloat = new();

    void CarregarListaBloat()
    {
        if (ListaBloat == null) return;

        // Configuração salva por versão antiga não tem IDs: ali vale o conjunto padrão,
        // que é exatamente o que a opção única removia. Assim ninguém abre o IsoForge
        // atualizado e encontra a lista inteira desmarcada sem ter mexido em nada.
        var escolhidos = _config.DebloatAppIds.Count > 0
            ? new HashSet<string>(_config.DebloatAppIds, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(Core.BloatCatalog.Padrao, StringComparer.OrdinalIgnoreCase);

        _bloat.Clear();
        foreach (var app in Core.BloatCatalog.Todos)
        {
            var vm = new Ui.BloatItemVm(app, escolhidos.Contains(app.Id));
            vm.PropertyChanged += (_, __) => AtualizarResumoBloat();
            _bloat.Add(vm);
        }

        var view = new System.Windows.Data.CollectionViewSource { Source = _bloat };
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(Ui.BloatItemVm.Categoria)));
        ListaBloat.ItemsSource = view.View;

        PanelBloat.Visibility = ChkDebloatApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AtualizarResumoBloat();
    }

    void AtualizarResumoBloat()
    {
        if (TxtBloatResumo == null) return;
        var n = _bloat.Count(b => b.Remover);
        TxtBloatResumo.Text = n == 0
            ? "Nenhum app marcado — nada será removido."
            : $"{n} de {_bloat.Count} apps serão removidos.";
    }

    void DebloatApps_Changed(object sender, RoutedEventArgs e)
    {
        if (PanelBloat == null) return;
        PanelBloat.Visibility = ChkDebloatApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void BloatTodos_Click(object sender, RoutedEventArgs e)
    {
        foreach (var b in _bloat) b.Remover = true;
    }

    void BloatNenhum_Click(object sender, RoutedEventArgs e)
    {
        foreach (var b in _bloat) b.Remover = false;
    }

    // ============ IMAGEM GOLDEN (DESATIVADA) ============
    // Comentado junto com a aba em MainWindow.xaml: o fluxo golden ainda nao esta
    // bem elaborado e nao funciona de forma confiavel. O codigo de apoio
    // (GoldenImageScripts, GoldenAutoBuilder) segue no repositorio; para reativar,
    // descomente este bloco e a aba correspondente.
    // void UpdateGoldenSummary()
    // {
    // if (TxtGoldenSummary == null) return;
    // bool offline = RbOfficeOffline?.IsChecked == true;
    // var hasOffice = _config.Apps.Any(a => a.Kind == AppKind.Office);
    // if (_config.Apps.Count == 0)
    // {
    // TxtGoldenSummary.Text = "Nenhum aplicativo escolhido ainda — vá à aba Aplicativos e adicione o que quer na imagem.";
    // TxtGoldenOfficeNote.Text = "";
    // return;
    // }
    // var names = _config.Apps.Select(a => a.Kind == AppKind.Office
    // ? $"Office 365 ({(offline ? "offline" : "online")})"
    // : a.Name);
    // TxtGoldenSummary.Text = "• " + string.Join("   • ", names);
    //
    // TxtGoldenOfficeNote.Text = hasOffice
    // ? (offline
    // ? "Office: será instalado a partir da fonte offline embutida (sem internet na VM)."
    // : "Office: a VM baixará o Office da internet durante a geração. Marque 'Offline' nas opções do Office para evitar isso.")
    // : "";
    // }

    void BrowseOfficeSource_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pasta com o Office baixado (contém setup.exe e Office\\Data)" };
        if (dlg.ShowDialog() == true) TxtOfficeSource.Text = dlg.FolderName;
    }

    async void DownloadOffice_Click(object sender, RoutedEventArgs e)
    {
        var office = _config.Apps.FirstOrDefault(a => a.Kind == AppKind.Office);
        var odt = office?.InstallerPath;
        if (string.IsNullOrWhiteSpace(odt) || !File.Exists(odt))
        {
            // Registra ANTES da caixa: sem isto o clique em "Baixar Office" não deixava
            // rastro nenhum, e o painel vazio parecia defeito do log.
            AppendLog("Baixar Office: nenhum Office 365 escolhido nos cards; não há o que baixar.");
            Caixa.Informar(this, "Selecione o Office 365 nos cards primeiro.");
            return;
        }
        var dlg = new OpenFolderDialog { Title = "Escolha onde baixar o Office (a pasta receberá setup.exe + Office\\Data)" };
        if (dlg.ShowDialog() != true) { AppendLog("Baixar Office: cancelado na escolha da pasta."); return; }

        var cfgXml = _config.OfficeConfigXml;
        var folder = dlg.FolderName;

        // Retomar o download parcial de OUTRA tentativa e a causa conhecida de o ODT
        // entrar em loop (valida o stream contra manifestos de outra versao e nunca
        // fecha). Como apagar e destrutivo, quem decide e o usuario — mas o padrao
        // sugerido e recomecar, que e o que funciona.
        var antes = OfficeDownloader.Inspecionar(folder);
        var limpar = false;
        if (antes.existe)
        {
            var r = Caixa.Perguntar3(this, "Download anterior encontrado", $"Esta pasta ja tem um download do Office: {antes.gb:F2} GB, o arquivo mais " +
                $"recente de {antes.maisNovo:dd/MM/yyyy HH:mm}." + Environment.NewLine + Environment.NewLine +
                "Retomar um download parcial de outra tentativa costuma travar o Office " +
                "Deployment Tool num laco sem fim." + Environment.NewLine + Environment.NewLine +
                "Recomecar do zero? (recomendado — apaga apenas a subpasta Office desta pasta)", "Recomeçar do zero", "Retomar", "Cancelar");

            if (r == MessageBoxResult.Cancel) { AppendLog("Baixar Office: cancelado na pergunta sobre o download anterior."); return; }
            limpar = r == MessageBoxResult.Yes;
        }
        SetBusy(true);
        var progress = new Progress<string>(AppendLog);
        var headline = new Progress<string>(s => Status($"Baixando Office offline: {s}"));
        try
        {
            AppendLog("==== Baixando Office offline ====");
            await Task.Run(() => OfficeDownloader.DownloadAsync(odt, cfgXml, folder, progress, CancellationToken.None, headline, limpar));
            Status("Office offline pronto");
            TxtOfficeSource.Text = folder;
            RbOfficeOffline.IsChecked = true;
            Caixa.Informar(this, "Office baixado. O modo offline foi ativado.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Interrompido a pedido. O processo em execucao foi encerrado.");
            MostrarEstado(Atividade.Parado, "Interrompido");
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO: {ex.Message}");
            MostrarEstado(Atividade.Falhou, "A geração falhou");
            VarrerConclusao(false);
            Caixa.Erro(this, ex.Message);
        }
        finally { SetBusy(false); }
    }

    // ============ IMAGEM GOLDEN (DESATIVADA) ============
    // Comentado junto com a aba em MainWindow.xaml: o fluxo golden ainda nao esta
    // bem elaborado e nao funciona de forma confiavel. O codigo de apoio
    // (GoldenImageScripts, GoldenAutoBuilder) segue no repositorio; para reativar,
    // descomente este bloco e a aba correspondente.
    // void Golden_Changed(object sender, RoutedEventArgs e)
    // {
    // if (PanelGolden == null) return;
    // PanelGolden.IsEnabled = ChkGolden.IsChecked == true;
    // }

    // ============ IMAGEM GOLDEN (DESATIVADA) ============
    // Comentado junto com a aba em MainWindow.xaml: o fluxo golden ainda nao esta
    // bem elaborado e nao funciona de forma confiavel. O codigo de apoio
    // (GoldenImageScripts, GoldenAutoBuilder) segue no repositorio; para reativar,
    // descomente este bloco e a aba correspondente.
    // void BrowseGoldenWim_Click(object sender, RoutedEventArgs e)
    // {
    // var dlg = new OpenFileDialog { Filter = "Imagem do Windows|*.wim", Title = "Selecione o install.wim capturado" };
    // if (dlg.ShowDialog() == true) TxtGoldenWim.Text = dlg.FileName;
    // }

    // ============ IMAGEM GOLDEN (DESATIVADA) ============
    // Comentado junto com a aba em MainWindow.xaml: o fluxo golden ainda nao esta
    // bem elaborado e nao funciona de forma confiavel. O codigo de apoio
    // (GoldenImageScripts, GoldenAutoBuilder) segue no repositorio; para reativar,
    // descomente este bloco e a aba correspondente.
    // void SaveGoldenScripts_Click(object sender, RoutedEventArgs e)
    // {
    // var dlg = new OpenFolderDialog { Title = "Escolha a pasta para salvar os scripts do fluxo golden" };
    // if (dlg.ShowDialog() != true) return;
    // File.WriteAllText(Path.Combine(dlg.FolderName, "Sysprep-Generalize.cmd"), GoldenImageScripts.Sysprep, System.Text.Encoding.UTF8);
    // File.WriteAllText(Path.Combine(dlg.FolderName, "Capture-GoldenImage.ps1"), GoldenImageScripts.Capture, System.Text.Encoding.UTF8);
    // File.WriteAllText(Path.Combine(dlg.FolderName, "LEIA-imagem-golden.txt"), GoldenImageScripts.Readme, System.Text.Encoding.UTF8);
    // AppendLog($"Scripts do fluxo golden salvos em: {dlg.FolderName}");
    // MessageBox.Show(this, "Scripts salvos:\n- Sysprep-Generalize.cmd\n- Capture-GoldenImage.ps1\n- LEIA-imagem-golden.txt", "IsoForge",
    // MessageBoxButton.OK, MessageBoxImage.Information);
    // }

    // ============ IMAGEM GOLDEN (DESATIVADA) ============
    // Comentado junto com a aba em MainWindow.xaml: o fluxo golden ainda nao esta
    // bem elaborado e nao funciona de forma confiavel. O codigo de apoio
    // (GoldenImageScripts, GoldenAutoBuilder) segue no repositorio; para reativar,
    // descomente este bloco e a aba correspondente.
    // async void GoldenAuto_Click(object sender, RoutedEventArgs e)
    // {
    // var cfg = CollectConfig();
    //
    // if (!GoldenAutoBuilder.IsAdministrator())
    // {
    // MessageBox.Show(this,
    // "Para gerar a imagem golden automaticamente, feche e reabra o IsoForge como Administrador (botão direito → Executar como administrador). O Hyper-V, o Mount-VHD e o DISM exigem elevação.",
    // "IsoForge — precisa de Administrador", MessageBoxButton.OK, MessageBoxImage.Warning);
    // return;
    // }
    //
    // if (string.IsNullOrWhiteSpace(cfg.SourceIsoPath) || !File.Exists(cfg.SourceIsoPath) ||
    // string.IsNullOrWhiteSpace(cfg.OutputIsoPath) ||
    // string.IsNullOrWhiteSpace(cfg.OscdimgPath) || !File.Exists(cfg.OscdimgPath) ||
    // string.IsNullOrWhiteSpace(cfg.UserName))
    // {
    // MessageBox.Show(this, "Antes de gerar a imagem golden, preencha: ISO de origem, ISO de saída, oscdimg e o usuário local (aba Sistema e usuário).",
    // "IsoForge", MessageBoxButton.OK, MessageBoxImage.Information);
    // return;
    // }
    //
    // var confirm = MessageBox.Show(this,
    // "Isso vai criar uma VM Hyper-V temporária, instalar tudo, capturar a imagem e gerar a ISO golden.\n\n" +
    // "Pode levar 30–60 minutos e usar ~40 GB de disco. Deseja continuar?",
    // "IsoForge — imagem golden automática", MessageBoxButton.YesNo, MessageBoxImage.Question);
    // if (confirm != MessageBoxResult.Yes) return;
    //
    // SetBusy(true);
    // _cts = new CancellationTokenSource();
    // var progress = new Progress<string>(AppendLog);
    // try
    // {
    // AppendLog("==== Iniciando geração automática de imagem golden ====");
    // await Task.Run(() => new GoldenAutoBuilder(progress).BuildAsync(cfg, _cts.Token));
    // MessageBox.Show(this, $"Imagem golden gerada:\n{cfg.OutputIsoPath}", "IsoForge",
    // MessageBoxButton.OK, MessageBoxImage.Information);
    // }
    // catch (Exception ex)
    // {
    // AppendLog($"ERRO: {ex.Message}");
    // MessageBox.Show(this, ex.Message, "IsoForge — erro", MessageBoxButton.OK, MessageBoxImage.Error);
    // }
    // finally { SetBusy(false); }
    // }

    // ------------------------------------------------------------------
    // Seleção de unidade
    // ------------------------------------------------------------------
    void UnitSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (PanelUnit == null) return;
        PanelUnit.IsEnabled = ChkUnitSelection.IsChecked == true;
        UpdateUnitPreview();
    }

    void Units_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        => Dispatcher.BeginInvoke(new Action(UpdateUnitPreview));

    void UpdateUnitPreview()
    {
        if (TxtUnitPreview == null) return;
        var first = _config.Units.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u.Prefix));
        var label = string.IsNullOrWhiteSpace(first?.Name) ? "Matriz" : first!.Name;
        var prefix = string.IsNullOrWhiteSpace(first?.Prefix) ? "MTZ" : first!.Prefix;
        TxtUnitPreview.Text = $"Escolhendo \"{label}\", a máquina se chamará {prefix}-XXXX (o final vem do nº de série do equipamento).";
    }

    void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkAutoLogon == null || ChkUnitSelection == null) return;
        bool entra = RbModeEntra.IsChecked == true;

        ChkAutoLogon.IsEnabled = !entra;
        if (ChkDemoteEntra != null) ChkDemoteEntra.IsEnabled = entra;

        if (RbUnitAudit != null && RbUnitFirstLogon != null)
        {
            RbUnitAudit.IsEnabled = !entra;
            if (entra) RbUnitFirstLogon.IsChecked = true;
        }
    }

    void RemoveUnit_Click(object sender, RoutedEventArgs e)
    {
        if (GridUnits.SelectedItem is UnitEntry u) _config.Units.Remove(u);
        UpdateUnitPreview();
    }

    void UnattendMode_Changed(object sender, RoutedEventArgs e)
    {
        if (TxtCustomUnattend == null) return;
        TxtCustomUnattend.IsEnabled = RbCustomUnattend.IsChecked == true;
    }

    void CmbEdition_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtProductKey == null) return;
        var tag = (CmbEdition.SelectedItem as ComboBoxItem)?.Tag as string;
        if (tag == "custom")
        {
            TxtProductKey.IsEnabled = true;
            TxtProductKey.Text = "";
        }
        else
        {
            TxtProductKey.IsEnabled = false;
            TxtProductKey.Text = tag ?? "";
        }
    }

    // Rola a página quando o mouse está sobre um DataGrid (que normalmente engole a roda).
    void Grid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        { RoutedEvent = UIElement.MouseWheelEvent, Source = sender };
        var parent = VisualTreeHelper.GetParent((DependencyObject)sender) as UIElement;
        parent?.RaiseEvent(args);
    }

    // ------------------------------------------------------------------
    // Build
    // ------------------------------------------------------------------
    BuildConfig CollectConfig()
    {
        _config.SourceIsoPath = TxtSourceIso.Text.Trim();
        _config.OutputIsoPath = TxtOutputIso.Text.Trim();
        _config.OscdimgPath = TxtOscdimg.Text.Trim();
        _config.NoPromptBoot = ChkNoPrompt.IsChecked == true;
        _config.AutoSelectDisk = ChkAutoDisk.IsChecked == true;
        _config.SkipWifiSetup = ChkSkipWifi.IsChecked == true;
        _config.AutoConnectWifi = ChkAutoWifi.IsChecked == true;
        _config.WifiSsid = TxtWifiSsid.Text.Trim();
        _config.WifiPassword = TxtWifiPassword.Text;

        // Driver: se a pasta local extraída não existe mais, não injeta (evita apontar p/ C:\Drivers vazio).
        if (!string.IsNullOrWhiteSpace(_config.DriverPackPath) && !Directory.Exists(_config.DriverPackPath))
            _config.DriverPackPath = "";
        // Categorias desmarcadas viram a lista de exclusão da injeção.
        if (_driverCategories.Count > 0)
            _config.DriverExcludedCategories = _driverCategories.Where(c => !c.Include).Select(c => c.Name).ToList();

        _config.Mode = RbModeEntra.IsChecked == true ? DeploymentMode.EntraId : DeploymentMode.LocalAccount;
        _config.DemoteEntraJoiner = ChkDemoteEntra.IsChecked == true;

        _config.UserName = TxtUserName.Text.Trim();
        _config.Password = TxtPassword.Text;
        _config.PasswordNeverExpires = ChkNeverExpires.IsChecked == true;
        _config.IsAdministrator = ChkAdmin.IsChecked == true;
        _config.AutoLogonOnce = ChkAutoLogon.IsChecked == true && _config.Mode != DeploymentMode.EntraId;
        _config.ComputerName = TxtComputerName.Text.Trim();

        _config.ProductKey = TxtProductKey.Text.Trim();
        _config.BypassHardwareChecks = ChkBypass.IsChecked == true;

        _config.UseUnitSelection = ChkUnitSelection.IsChecked == true;
        _config.UnitMethod = (RbUnitAudit.IsChecked == true && _config.Mode != DeploymentMode.EntraId)
            ? UnitSelectionMethod.Audit
            : UnitSelectionMethod.FirstLogon;

        _config.OfficeOffline = RbOfficeOffline.IsChecked == true;
        _config.OfficeSourceFolder = TxtOfficeSource.Text.Trim();

        // IMAGEM GOLDEN (DESATIVADA): os campos seguem em BuildConfig, so nao ha mais UI para eles.
        // _config.UseCapturedWim = ChkGolden.IsChecked == true;
        // _config.CapturedWimPath = TxtGoldenWim.Text.Trim();
        _config.WallpaperPath = TxtWallpaper.Text.Trim();
        _config.LockScreenPath = TxtLockScreen.Text.Trim();
        _config.WindowsTheme = RbThemeLight.IsChecked == true ? WindowsThemeMode.Light
            : RbThemeDark.IsChecked == true ? WindowsThemeMode.Dark
            : WindowsThemeMode.Default;
        _config.TaskbarAlign = RbTaskbarCenter.IsChecked == true ? TaskbarAlignment.Center
            : RbTaskbarLeft.IsChecked == true ? TaskbarAlignment.Left
            : TaskbarAlignment.Default;
        _config.DebloatRemoveApps = ChkDebloatApps.IsChecked == true;
        _config.DebloatAppIds = _bloat.Where(b => b.Remover).Select(b => b.Id).ToList();
        _config.DebloatDisableCopilot = ChkDebloatCopilot.IsChecked == true;
        _config.DebloatRemoveTeamsChat = ChkDebloatTeams.IsChecked == true;
        _config.DebloatRemoveOneDrive = ChkDebloatOneDrive.IsChecked == true;
        _config.DebloatDisableStartAds = ChkDebloatAds.IsChecked == true;
        _config.DebloatDisableTelemetry = ChkDebloatTelemetry.IsChecked == true;
        _config.GenerateReport = ChkReport.IsChecked == true;
        _config.FortiClientRegImportPath = TxtFortiReg.Text.Trim();
        _config.VpnUseTextImport = ChkVpnTextImport.IsChecked == true;
        _config.VpnXAuth = RbXauthSave.IsChecked == true ? VpnXAuthMode.Save
            : RbXauthOff.IsChecked == true ? VpnXAuthMode.Disabled
            : VpnXAuthMode.Prompt;
        _config.XAuthUsername = TxtXauthUser.Text.Trim();
        _config.XAuthPassword = TxtXauthPass.Text;

        _config.UseCustomUnattend = RbCustomUnattend.IsChecked == true;
        _config.CustomUnattendPath = TxtCustomUnattend.Text.Trim();
        _config.PostScriptPath = TxtPostScript.Text.Trim();

        CollectLinuxConfig();

        // Persiste localmente a cada coleta.
        SettingsStore.Save(_config);
        return _config;
    }

    /// <summary>Preenche a interface a partir da configuração carregada (inverso do CollectConfig).</summary>
    void ApplyConfigToUi()
    {
        TxtSourceIso.Text = _config.SourceIsoPath;
        TxtOutputIso.Text = _config.OutputIsoPath;
        ChkNoPrompt.IsChecked = _config.NoPromptBoot;
        ChkAutoDisk.IsChecked = _config.AutoSelectDisk;
        ChkSkipWifi.IsChecked = _config.SkipWifiSetup;
        ChkAutoWifi.IsChecked = _config.AutoConnectWifi;
        TxtWifiSsid.Text = _config.WifiSsid;
        TxtWifiPassword.Text = _config.WifiPassword;
        PanelWifi.IsEnabled = _config.AutoConnectWifi;

        if (!string.IsNullOrWhiteSpace(_config.DriverModelName))
        {
            TxtDriverStatus.Text = Directory.Exists(_config.DriverPackPath)
                ? $"Driver pronto: {_config.DriverModelName}. Escolha os componentes abaixo."
                : $"Driver do modelo {_config.DriverModelName} precisa ser baixado de novo (arquivos locais removidos).";
            // Reconstrói as categorias em segundo plano (aplica as exclusões salvas).
            if (Directory.Exists(_config.DriverPackPath))
                _ = RefreshDriverCategoriesAsync(new HashSet<string>(_config.DriverExcludedCategories, StringComparer.OrdinalIgnoreCase));
        }

        RbModeEntra.IsChecked = _config.Mode == DeploymentMode.EntraId;
        RbModeLocal.IsChecked = _config.Mode != DeploymentMode.EntraId;
        ChkDemoteEntra.IsChecked = _config.DemoteEntraJoiner;

        TxtUserName.Text = _config.UserName;
        TxtPassword.Text = _config.Password;
        ChkNeverExpires.IsChecked = _config.PasswordNeverExpires;
        ChkAdmin.IsChecked = _config.IsAdministrator;
        ChkAutoLogon.IsChecked = _config.AutoLogonOnce;
        TxtComputerName.Text = _config.ComputerName;

        if (!string.IsNullOrWhiteSpace(_config.ProductKey))
        {
            foreach (var obj in CmbEdition.Items)
                if (obj is ComboBoxItem it && (it.Tag as string) == "custom") { CmbEdition.SelectedItem = it; break; }
            TxtProductKey.IsEnabled = true;
            TxtProductKey.Text = _config.ProductKey;
        }
        ChkBypass.IsChecked = _config.BypassHardwareChecks;

        ChkUnitSelection.IsChecked = _config.UseUnitSelection;
        RbUnitAudit.IsChecked = _config.UnitMethod == UnitSelectionMethod.Audit;
        RbUnitFirstLogon.IsChecked = _config.UnitMethod != UnitSelectionMethod.Audit;
        if (PanelUnit != null) PanelUnit.IsEnabled = _config.UseUnitSelection;

        // Office
        RbOfficeOffline.IsChecked = _config.OfficeOffline;
        RbOfficeOnline.IsChecked = !_config.OfficeOffline;
        TxtOfficeSource.Text = _config.OfficeSourceFolder;
        foreach (var obj in CmbOfficeLang.Items)
            if (obj is ComboBoxItem it && (it.Tag as string) == _config.OfficeLanguage) { CmbOfficeLang.SelectedItem = it; break; }

        // IMAGEM GOLDEN (DESATIVADA)
        // ChkGolden.IsChecked = _config.UseCapturedWim;
        // TxtGoldenWim.Text = _config.CapturedWimPath;

        TxtWallpaper.Text = _config.WallpaperPath;
        TxtLockScreen.Text = _config.LockScreenPath;
        RbThemeLight.IsChecked = _config.WindowsTheme == WindowsThemeMode.Light;
        RbThemeDark.IsChecked = _config.WindowsTheme == WindowsThemeMode.Dark;
        RbThemeDefault.IsChecked = _config.WindowsTheme == WindowsThemeMode.Default;
        RbTaskbarCenter.IsChecked = _config.TaskbarAlign == TaskbarAlignment.Center;
        RbTaskbarLeft.IsChecked = _config.TaskbarAlign == TaskbarAlignment.Left;
        RbTaskbarDefault.IsChecked = _config.TaskbarAlign == TaskbarAlignment.Default;
        ChkDebloatApps.IsChecked = _config.DebloatRemoveApps;
        CarregarListaBloat();
        ChkDebloatCopilot.IsChecked = _config.DebloatDisableCopilot;
        ChkDebloatTeams.IsChecked = _config.DebloatRemoveTeamsChat;
        ChkDebloatOneDrive.IsChecked = _config.DebloatRemoveOneDrive;
        ChkDebloatAds.IsChecked = _config.DebloatDisableStartAds;
        ChkDebloatTelemetry.IsChecked = _config.DebloatDisableTelemetry;
        ChkReport.IsChecked = _config.GenerateReport;
        ThemeService.Apply(_config.AppDarkTheme);
        ThemeService.ApplyTitleBar(this, _config.AppDarkTheme);
        // Depois do tema, sempre: o ThemeService nao escreve as chaves OsAccent*,
        // e sem esta linha alternar o tema apagaria a marca do sistema.
        OsAccentService.Apply(_config.Os, _config.AppDarkTheme);
        UpdateThemeButton();
        TxtFortiReg.Text = _config.FortiClientRegImportPath;
        ChkVpnTextImport.IsChecked = _config.VpnUseTextImport;
        RbXauthSave.IsChecked = _config.VpnXAuth == VpnXAuthMode.Save;
        RbXauthOff.IsChecked = _config.VpnXAuth == VpnXAuthMode.Disabled;
        RbXauthPrompt.IsChecked = _config.VpnXAuth == VpnXAuthMode.Prompt;
        TxtXauthUser.Text = _config.XAuthUsername;
        TxtXauthPass.Text = _config.XAuthPassword;

        RbCustomUnattend.IsChecked = _config.UseCustomUnattend;
        RbGeneratedUnattend.IsChecked = !_config.UseCustomUnattend;
        TxtCustomUnattend.Text = _config.CustomUnattendPath;
        TxtPostScript.Text = _config.PostScriptPath;

        ApplyLinuxConfigToUi();
    }

    // ------------------------------------------------------------------
    // Perfis de configuração nomeados
    // ------------------------------------------------------------------
    bool _loadingProfiles;

    void RefreshProfiles(string? select = null)
    {
        _loadingProfiles = true;
        CmbProfile.ItemsSource = SettingsStore.ListProfiles();
        if (select != null) CmbProfile.SelectedItem = select;
        _loadingProfiles = false;
    }

    void Profile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfiles || CmbProfile.SelectedItem is not string name) return;
        var cfg = SettingsStore.LoadProfile(name);
        if (cfg == null) return;
        _config = cfg;
        GridVpn.ItemsSource = _config.VpnTunnels;
        GridUnits.ItemsSource = _config.Units;
        _config.Apps.CollectionChanged += (_, __) => Dispatcher.Invoke(() => { /* IMAGEM GOLDEN (DESATIVADA): UpdateGoldenSummary(); */ UpdateDynamicConfigCards(); });
        ApplyConfigToUi();
        // O perfil pode ter sido salvo para outro sistema operacional: a interface acompanha.
        ApplyOsToUi();
        RefreshAppCards();
        UpdateUnitPreview();
        // IMAGEM GOLDEN (DESATIVADA): UpdateGoldenSummary();
        AppendLog($"Perfil carregado: {name} ({OsCatalog.NameOf(_config.Os)}).");
    }

    void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (CmbProfile.SelectedItem is string name)
        {
            SettingsStore.SaveProfile(name, CollectConfig());
            AppendLog($"Perfil salvo: {name}");
        }
        else SaveProfileAs_Click(sender, e);
    }

    void SaveProfileAs_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("Nome do perfil (ex.: Matriz, Cliente X):", "Salvar perfil",
            CmbProfile.SelectedItem as string ?? "");
        if (string.IsNullOrWhiteSpace(name)) return;
        SettingsStore.SaveProfile(name, CollectConfig());
        RefreshProfiles(select: name);
        AppendLog($"Perfil salvo: {name}");
    }

    void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (CmbProfile.SelectedItem is not string name) return;
        if (Caixa.Perguntar(this, "", $"Excluir o perfil \"{name}\"?", "Excluir", "Manter") != MessageBoxResult.Yes) return;
        SettingsStore.DeleteProfile(name);
        RefreshProfiles();
        AppendLog($"Perfil excluído: {name}");
    }

    /// <summary>Caixa de entrada simples (sem dependências externas).</summary>
    string? Prompt(string message, string title, string def = "")
    {
        var win = new Window
        {
            Title = title, Width = 400, SizeToContent = SizeToContent.Height, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize
        };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        var tb = new TextBox { Text = def };
        sp.Children.Add(tb);
        var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        string? result = null;
        var ok = new Button { Content = "OK", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, __) => { result = tb.Text; win.DialogResult = true; };
        var cancel = new Button { Content = "Cancelar", Width = 90, IsCancel = true };
        btns.Children.Add(ok); btns.Children.Add(cancel);
        sp.Children.Add(btns);
        win.Content = sp;
        tb.Loaded += (_, __) => { tb.Focus(); tb.SelectAll(); };
        return win.ShowDialog() == true ? result : null;
    }

    /// <summary>
    /// Depois da ISO pronta, oferece gravá-la num pendrive.
    ///
    /// É o passo seguinte natural — quem gera a imagem quase sempre vai instalar em
    /// alguma máquina — e perguntar aqui poupa reabrir o programa, reencontrar o arquivo
    /// e repetir a escolha do disco. Quem só queria o .iso responde "Agora não" e segue.
    ///
    /// Grava a ISO JÁ GERADA (monta e copia); não refaz o provisionamento, que produziria
    /// exatamente os mesmos arquivos em mais 20 minutos.
    /// </summary>
    async Task OfereceGravarPendriveAsync(string isoPath, bool reaberto = false)
    {
        var gb = 0.0;
        try { gb = new FileInfo(isoPath).Length / 1024.0 / 1024.0 / 1024.0; } catch { }

        var r = reaberto
            // Reaberto como administrador: a pessoa já disse que queria gravar. O caminho
            // aparece por extenso porque veio de fora do processo — ela confirma O QUE vai
            // ser gravado, e não um "sim" genérico.
            ? Caixa.Perguntar(this, "IsoForge reaberto como administrador",
                $"Continuar a gravação desta imagem?\n\n{isoPath}" + (gb > 0 ? $"\n({gb:F2} GB)" : ""),
                rotuloSim: "Escolher o pendrive", rotuloNao: "Agora não")
            : Caixa.Perguntar(this, "ISO gerada com sucesso",
                $"A imagem está em {isoPath}" + (gb > 0 ? $" ({gb:F2} GB)." : ".") +
                "\n\nPosso prepará-la agora num pendrive inicializável. O conteúdo do pendrive será apagado.",
                rotuloSim: "Gravar em pendrive", rotuloNao: "Agora não", tipo: TipoCaixa.Sucesso);
        if (r != MessageBoxResult.Yes) return;

        var escolha = new UsbPickerWindow { Owner = this };
        escolha.ShowDialog();

        if (escolha.PediuElevacao)
        {
            // A ISO já existe em disco: o caminho dela é a única coisa que atravessa para a
            // instância elevada, e como dica — ela reapresenta a escolha do pendrive.
            await EntregarBastaoAsync($"--pendrive {Elevacao.Citar(Path.GetFullPath(isoPath))}");
            return;
        }
        if (escolha.Escolhido is not { } alvo) return;

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(AppendLog);
        // O marco vira nome de etapa: os numeros do gravador sao pontos (78 formatado,
        // 82 WIM partido, 98 copia feita), nao medida continua. Quem informa e o texto.
        // O clamp existe porque Progress<T>.Report e assincrono: um tique atrasado pode
        // chegar depois do marco seguinte e fazer a barra andar para tras.
        var percent = new Progress<int>(p => Dispatcher.Invoke(() =>
        {
            if (p > BuildProgress.Value) BuildProgress.Value = p;
            EtapaDoPendrive(p);
        }));
        BuildProgress.Value = 0;
        BuildProgressPanel.Visibility = Visibility.Visible;
        try
        {
            AppendLog($"==== Gravando a ISO em {alvo.Rotulo} ====");
            Etapa($"Preparando os arquivos — {alvo.Nome}");
            await Task.Run(() => new IsoPipeline(progress, percent)
                .GravarIsoEmPendriveAsync(isoPath, alvo, _cts.Token));
            MostrarEstado(Atividade.Concluido, "Pendrive pronto");
            Caixa.Concluir(this, "Pendrive pronto", $"{alvo.Rotulo} está pronto para instalar.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Interrompido a pedido. O processo em execucao foi encerrado.");
            MostrarEstado(Atividade.Parado, "Interrompido");
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO: {ex.Message}");
            MostrarEstado(Atividade.Falhou, "Falhou");
            Caixa.Erro(this, "Não consegui gravar o pendrive", ex.Message);
        }
        finally
        {
            SetBusy(false);
            BuildProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Reabre o IsoForge como administrador e encerra esta instância.
    ///
    /// Particionar e formatar são operações privilegiadas: não há como o processo atual
    /// ganhar esse direito depois de nascer. Em vez de mandar a pessoa fechar e reabrir com
    /// o botão direito — que é o programa sabendo o que fazer e pedindo que o façam por ele
    /// —, o IsoForge se relança.
    /// </summary>
    async Task EntregarBastaoAsync(string argumentos)
    {
        // Já viemos de uma elevação e ainda não somos administrador: relançar de novo só
        // repetiria o aviso do Windows sem fim. Melhor dizer o que aconteceu.
        if (App.ModoPendrive && !UsbWriter.EhAdministrador())
        {
            Caixa.Erro(this, "A elevação não pegou",
                "O IsoForge foi reaberto para pedir permissão de administrador, mas continua "
                + "sem ela. Isso costuma ser política da máquina.\n\n"
                + "Fale com quem administra o computador, ou abra o IsoForge com o botão "
                + "direito → Executar como administrador.");
            return;
        }

        // Nunca entregue o bastão com trabalho em andamento: encerrar aqui mataria a thread
        // no meio, e um pendrive gravado pela metade não avisa que está pela metade.
        if (_ocupado)
        {
            Caixa.Avisar(this, "Há uma tarefa em andamento",
                "Espere ela terminar (ou cancele) antes de reabrir como administrador.");
            return;
        }

        // A configuração precisa estar em disco ANTES de fechar: é por ela, cifrada, que a
        // instância elevada recebe o que estava preenchido na tela.
        try { CollectConfig(); } catch { /* melhor esforço: não impedir a elevação */ }

        var (resultado, erro) = Elevacao.Relancar(argumentos);
        switch (resultado)
        {
            case ResultadoElevacao.UsuarioRecusou:
                // Recusar o aviso do Windows é escolha, não falha. A janela continua viva.
                Caixa.Informar(this, "Sem permissão de administrador",
                    "Sem administrador não dá para particionar nem formatar o pendrive.\n\n"
                    + "A tela continua como estava — você pode tentar de novo quando quiser.");
                return;

            case ResultadoElevacao.SemCaminho:
                Caixa.Erro(this, "Não consegui identificar o executável do IsoForge",
                    "Abra o IsoForge com o botão direito → Executar como administrador.");
                return;

            case ResultadoElevacao.Falhou:
                Caixa.Erro(this, "Não consegui reabrir como administrador",
                    "Abra o IsoForge com o botão direito → Executar como administrador.", erro);
                return;
        }

        // Só depois de a nova instância ter nascido é que esta sai — e sai sem limpar o
        // cache nem regravar a configuração, que a outra já está usando.
        App.EntregandoBastao = true;
        await Task.Yield();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Grava direto no pendrive, sem gerar .iso. Mesmo pipeline até a injeção dos
    /// arquivos; o que muda é o destino.
    /// </summary>
    async void BuildUsb_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectConfig();
        if (cfg.IsLinux)
        {
            // O caminho do Linux é outro: a ISO das distribuições é híbrida e vai para o
            // pendrive como imagem bruta (setor a setor), não como cópia de arquivos.
            // Prometer o mesmo botão para os dois entregaria um pendrive que não arranca.
            Caixa.Informar(this, "Gravar em pendrive está disponível por enquanto só para Windows.\n\n" +
                "As ISOs de Linux são híbridas e precisam ser gravadas como imagem bruta, " +
                "que é um caminho diferente. Gere a ISO e use o Rufus, o balenaEtcher ou " +
                "o comando dd para essas.");
            return;
        }

        var escolha = new UsbPickerWindow { Owner = this };
        escolha.ShowDialog();

        if (escolha.PediuElevacao)
        {
            // Sem caminho de ISO: aqui o pipeline inteiro ainda vai rodar, e a configuração
            // viaja pelo settings.dat cifrado, nunca pela linha de comando.
            await EntregarBastaoAsync("--pendrive");
            return;
        }
        if (escolha.Escolhido is not { } alvo) return;

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(AppendLog);
        // O marco vira nome de etapa: os numeros do gravador sao pontos (78 formatado,
        // 82 WIM partido, 98 copia feita), nao medida continua. Quem informa e o texto.
        // O clamp existe porque Progress<T>.Report e assincrono: um tique atrasado pode
        // chegar depois do marco seguinte e fazer a barra andar para tras.
        var percent = new Progress<int>(p => Dispatcher.Invoke(() =>
        {
            if (p > BuildProgress.Value) BuildProgress.Value = p;
            EtapaDoPendrive(p);
        }));
        BuildProgress.Value = 0;
        BuildProgressPanel.Visibility = Visibility.Visible;
        try
        {
            AppendLog($"==== Gravando no pendrive: {alvo.Rotulo} ====");
            Etapa($"Preparando os arquivos — {alvo.Nome}");
            await Task.Run(() => new IsoPipeline(progress, percent).BuildToUsbAsync(cfg, alvo, _cts.Token));
            MostrarEstado(Atividade.Concluido, "Pendrive pronto");
            VarrerConclusao(true);
            Caixa.Informar(this, $"Pendrive pronto para instalar:\n{alvo.Rotulo}");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Interrompido a pedido. O processo em execucao foi encerrado.");
            MostrarEstado(Atividade.Parado, "Interrompido");
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO: {ex.Message}");
            MostrarEstado(Atividade.Falhou, "Falhou");
            Caixa.Erro(this, ex.Message);
        }
        finally
        {
            SetBusy(false);
            BuildProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    async void Build_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectConfig();
        SetBusy(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(AppendLog);
        var percent = new Progress<int>(p => Dispatcher.Invoke(() =>
        {
            // Nunca para tras: Progress<T>.Report e assincrono e um tique atrasado pode
            // chegar depois do proximo marco.
            if (p > BuildProgress.Value) BuildProgress.Value = p;
            try { Taskbar.ProgressValue = p / 100.0; } catch { }
        }));
        BuildProgress.Value = 0;
        BuildProgressPanel.Visibility = Visibility.Visible;
        try
        {
            AppendLog($"==== Iniciando geração da ISO ({OsCatalog.NameOf(cfg.Os)}) ====");
            if (cfg.IsLinux)
                await Task.Run(() => new LinuxIsoPipeline(progress, percent).BuildAsync(cfg, _cts.Token));
            else
                await Task.Run(() => new IsoPipeline(progress, percent).BuildAsync(cfg, _cts.Token));
            // O visto e a varredura ANTES da caixa de dialogo: a caixa bloqueia a
            // interface e a conclusao tem de estar visivel quando ela sair.
            MostrarEstado(Atividade.Concluido, "ISO gerada");
            VarrerConclusao(true);
            await OfereceGravarPendriveAsync(cfg.OutputIsoPath);
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO: {ex.Message}");
            Caixa.Erro(this, ex.Message);
        }
        finally
        {
            SetBusy(false);
            MostrarProgresso(false, atrasoMs: 1200);
        }
    }

    void DryRun_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectConfig();
        var dlg = new OpenFolderDialog { Title = "Escolha a pasta onde gerar os arquivos de teste" };
        if (dlg.ShowDialog() != true) return;

        var progress = new Progress<string>(AppendLog);
        try
        {
            AppendLog("==== Gerando arquivos (teste, sem ISO) ====");
            if (cfg.IsLinux)
                new LinuxIsoPipeline(progress).DryRun(cfg, dlg.FolderName);
            else
                new IsoPipeline(progress).DryRun(cfg, dlg.FolderName);
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO: {ex.Message}");
            Caixa.Erro(this, ex.Message);
        }
    }

    async void TestSandbox_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectConfig();

        // Office ONLINE baixa via BITS/Delivery Optimization — que o Windows Sandbox não suporta
        // de forma confiável (erro 30183). Avisa antes para o usuário não achar que é bug da ISO.
        bool officeOnline = cfg.Apps.Any(a => a.Kind == AppKind.Office) && !cfg.OfficeOffline;
        if (officeOnline)
        {
            var r = Caixa.Perguntar(this, "", "O Office no modo ONLINE baixa da internet via BITS, que o Windows Sandbox não suporta " +
                "bem (dá o erro 30183 \"couldn't install / download a required file\"), mesmo com internet.\n\n" +
                "Isso é uma limitação do Sandbox — numa máquina real ou VM Hyper-V o Office online instala normal.\n\n" +
                "Para testar o Office, prefira:\n" +
                "  • Office OFFLINE (embute o Office na ISO) — funciona no Sandbox; ou\n" +
                "  • o botão \"Script Hyper-V\" (testa o fluxo online numa VM de verdade).\n\n" +
                "Deseja continuar o teste no Sandbox mesmo assim? (os outros apps instalam normalmente)", "Sim", "Nao");
            if (r != MessageBoxResult.Yes) return;
        }

        // FORA de %TEMP%\IsoForge, de proposito: o CacheCleaner apaga aquela pasta INTEIRA
        // quando o IsoForge fecha (Core/CacheCleaner.cs). Como ela e o C:\Setup mapeado
        // dentro do Sandbox, fechar o IsoForge com o Sandbox aberto puxava o tapete —
        // os 3,6 GB do Office sumiam enquanto o ODT estava lendo deles. Aqui o
        // CacheCleaner so limpa a subpasta Drivers.
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                  "IsoForge", "SandboxTest");
        var progress = new Progress<string>(AppendLog);
        SetBusy(true);
        try
        {
            AppendLog("==== Preparando teste no Windows Sandbox ====");
            // Nao engole a falha: se o Sandbox anterior ainda segura a pasta, o teste
            // rodaria sobre o payload VELHO e o resultado nao valeria nada.
            if (Directory.Exists(folder))
            {
                try { Directory.Delete(folder, true); }
                catch (Exception ex)
                {
                    AppendLog($"Atenção: não consegui limpar {folder} ({ex.Message}). Feche o Windows Sandbox "
                              + "da rodada anterior antes de testar de novo — senão o teste roda sobre o payload antigo.");
                }
            }
            string wsb = "";
            await Task.Run(() => wsb = new IsoPipeline(progress).PrepareSandbox(cfg, folder));

            if (!IsoPipeline.IsSandboxAvailable())
            {
                AppendLog($"Windows Sandbox não habilitado. Arquivos de teste em: {folder}");
                Caixa.Informar(this, "O Windows Sandbox não está habilitado nesta máquina.\n\n" +
                    "Habilite (PowerShell como Administrador e reinicie):\n" +
                    "Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All\n\n" +
                    $"Os arquivos de teste já foram gerados em:\n{folder}\n\n" +
                    "Depois de habilitar, dê dois cliques em Testar-Sandbox.wsb.");
                return;
            }

            AppendLog("Abrindo o Windows Sandbox — instala os apps numa cópia descartável (igual à VM).");
            AppendLog("Dentro do Sandbox: a janela do install.cmd abre sozinha; ao terminar, veja C:\\Setup\\install.log.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(wsb) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO: {ex.Message}");
            Caixa.Erro(this, ex.Message);
        }
        finally { SetBusy(false); }
    }

    void SaveTestScript_Click(object sender, RoutedEventArgs e)
    {
        bool linux = _config.IsLinux;
        var fileName = linux ? LinuxTestScripts.FileName : "Testar-HyperV.ps1";
        var dlg = new SaveFileDialog
        {
            Filter = "Script PowerShell|*.ps1",
            FileName = fileName,
            Title = "Salvar script de teste do Hyper-V"
        };
        if (dlg.ShowDialog() != true) return;

        File.WriteAllText(dlg.FileName, linux ? LinuxTestScripts.HyperV : TestScripts.HyperV, System.Text.Encoding.UTF8);
        AppendLog($"Script de teste salvo: {dlg.FileName}");
        AppendLog($"Execute em um PowerShell como Administrador: .\\{System.IO.Path.GetFileName(dlg.FileName)} -IsoPath \"caminho\\da\\ISO.iso\"");
        if (linux)
            AppendLog("A VM é criada com o Secure Boot no template da autoridade UEFI da Microsoft (exigido pelo Linux).");
    }

    /// <summary>Há trabalho rodando. Quem precisa saber: a entrega do bastão para a instância elevada.</summary>
    bool _ocupado;

    void SetBusy(bool busy)
    {
        _ocupado = busy;
        if (BtnCancelarTarefa != null) BtnCancelarTarefa.IsEnabled = busy;
        BtnBuild.IsEnabled = !busy;
        BtnDryRun.IsEnabled = !busy;
        BtnTestScript.IsEnabled = !busy;
        if (BtnSandbox != null) BtnSandbox.IsEnabled = !busy;
        if (!busy) HideDownloadBar();
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;

        // O indicador so volta a "parado" se nao estamos exibindo um resultado:
        // um "ISO gerada" que dura meio segundo nao informa ninguem.
        //
        // E, ao ficar ocupado, o texto NAO e herdado: reaproveitar o anterior deixava o
        // cabecalho dizendo "Pronto" durante uma gravacao de quinze minutos — a tela
        // mentindo exatamente no momento em que a pessoa mais olha para ela.
        if (busy)
        {
            var texto = _atividade == Atividade.Ocupado ? TxtHeaderStatus.Text : "Trabalhando...";
            MostrarEstado(Atividade.Ocupado, texto);
            // O console e o unico lugar onde o andamento aparece. Se ele estiver recolhido
            // — e em tela baixa ele se recolhe sozinho ao abrir —, a pessoa fica sem canal
            // nenhum e conclui que travou. Enquanto ha trabalho, ele abre.
            if (_logRecolhido) { _logAbertoPorTrabalho = true; SetLogCollapsed(false); }
        }
        else
        {
            // E devolve o espaco depois. Sem isto, a tela de 768 ficaria amputada para
            // sempre depois da primeira gravacao.
            if (_logAbertoPorTrabalho) { _logAbertoPorTrabalho = false; SetLogCollapsed(true); }
            PararEtapa();
            if (_atividade == Atividade.Ocupado) MostrarEstado(Atividade.Parado, "Pronto");
        }
    }

    /// <summary>
    /// Interrompe a tarefa em andamento — de verdade: o cancelamento chega ao processo
    /// filho e o mata, junto da árvore dele.
    ///
    /// A confirmação não é cerimônia. Parar no meio de uma gravação deixa o pendrive
    /// inutilizável até ser formatado de novo, e isso precisa estar escrito antes, não
    /// descoberto depois.
    /// </summary>
    void CancelarTarefa_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not { IsCancellationRequested: false } cts) return;

        var r = Caixa.Perguntar(this, "Interromper a tarefa?",
            "A tarefa em andamento será interrompida agora.\n\n"
            + "Se for uma gravação em pendrive, a mídia fica pela metade e inutilizável até "
            + "ser formatada de novo. Se for a geração de uma ISO, o arquivo de saída não "
            + "chega a ser criado.",
            rotuloSim: "Interromper", rotuloNao: "Continuar a tarefa");
        if (r != MessageBoxResult.Yes) return;

        BtnCancelarTarefa.IsEnabled = false;
        AppendLog("Interrompendo a pedido...");
        Etapa("Interrompendo");
        try { cts.Cancel(); } catch { /* já descartado: a tarefa terminou sozinha */ }
    }

    // ==================================================================
    // Relógio de etapa
    //
    // A queixa que originou isto: a barra parou em 82% e a pessoa achou que o programa
    // tinha travado. Não tinha — estava copiando 12 GB para um pendrive, um passo que
    // leva de 5 a 20 minutos e que ia de 82 a 98 sem dizer nada.
    //
    // A saída NÃO é uma porcentagem melhor. A faixa tem 16 pontos: mesmo uma medida
    // perfeita mudaria o número uma vez por minuto, o que continua lendo como travado.
    // Pior, medir o espaço ocupado no destino mentiria — o FAT32 aloca a cadeia inteira
    // de um arquivo de 3,8 GB de uma vez, a barra saltaria e congelaria de novo, só que
    // em 96%.
    //
    // O que não mente é o tempo decorrido. Então a barra fica indeterminada (pulsando,
    // que é honesto: "estou trabalhando, não sei quanto falta"), o número some da tela, e
    // o cabeçalho diz o nome da etapa e há quanto tempo ela está rodando.
    // ==================================================================

    readonly System.Windows.Threading.DispatcherTimer _relogioEtapa = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    string _etapaTexto = "";
    DateTime _etapaDesde;

    /// <summary>
    /// Começa uma etapa: nome no cabeçalho e relógio correndo.
    ///
    /// <paramref name="medido"/> diz se existe uma medida honesta por trás. Havendo, a
    /// barra é determinada e a porcentagem aparece; não havendo, ela pulsa e o número sai
    /// de cena — porque um número parado durante quinze minutos é pior que número nenhum.
    /// </summary>
    void Etapa(string texto, bool medido = false)
    {
        _etapaTexto = texto;
        _etapaDesde = DateTime.Now;
        Indeterminado(!medido);
        AppendLog($"— {texto}");
        AtualizarRelogio();

        if (!_relogioEtapa.IsEnabled)
        {
            _relogioEtapa.Tick -= RelogioTick;
            _relogioEtapa.Tick += RelogioTick;
            _relogioEtapa.Start();
        }
    }

    void RelogioTick(object? s, EventArgs e) => AtualizarRelogio();

    void AtualizarRelogio()
    {
        if (string.IsNullOrEmpty(_etapaTexto)) return;
        var d = DateTime.Now - _etapaDesde;
        var tempo = d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"mm\:ss");
        MostrarEstado(Atividade.Ocupado, $"{_etapaTexto} · {tempo}");
    }

    void PararEtapa()
    {
        _relogioEtapa.Stop();
        _etapaTexto = "";
        Indeterminado(false);
        try { Taskbar.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None; } catch { }
    }

    /// <summary>
    /// Barra pulsando em vez de número. O número sai de cena junto: uma barra
    /// indeterminada ao lado de um "82%" parado é a tela se contradizendo.
    /// </summary>
    void Indeterminado(bool sim)
    {
        BuildProgress.IsIndeterminate = sim;
        TxtBuildPct.Visibility = sim ? Visibility.Collapsed : Visibility.Visible;
        try
        {
            Taskbar.ProgressState = sim
                ? System.Windows.Shell.TaskbarItemProgressState.Indeterminate
                : System.Windows.Shell.TaskbarItemProgressState.Normal;
        }
        catch { /* a barra de tarefas é conforto: nunca derrubar o trabalho por ela */ }
    }

    /// <summary>
    /// Traduz o percentual que o gravador reporta no nome da etapa que começou.
    ///
    /// Os números do UsbWriter são marcos, não medida contínua: 78 é o disco formatado,
    /// 82 é o WIM partido, 98 é a cópia terminada. Cada um deles marca o INÍCIO do passo
    /// seguinte — que é justamente o que a pessoa precisa saber.
    /// </summary>
    void EtapaDoPendrive(int marco)
    {
        switch (marco)
        {
            // Os dois passos longos são medidos pelos bytes que o processo filho escreve
            // — contador do próprio Windows, monotônico e independente de idioma.
            case 78: Etapa("Partindo o install.wim (não cabe em FAT32)", medido: true); break;
            case 82: Etapa("Copiando os arquivos para o pendrive", medido: true); break;
            case 98: Etapa("Finalizando e limpando os arquivos temporários"); break;
        }
    }

    /// <summary>
    /// Recolhe/expande o console. Em telas baixas ele consumia a altura que o
    /// formulário precisava mesmo estando vazio; quem não está acompanhando o log
    /// fecha e recupera o espaço.
    /// </summary>
    void ToggleLog_Click(object sender, RoutedEventArgs e) => SetLogCollapsed(!_logRecolhido);

    /// <summary>
    /// Recolhe o console. Esconde o PAINEL, nao so o texto: a barrinha de titulo
    /// dele custava 90 dos 119 px que o recolhimento deveria devolver ao
    /// formulario. Com o painel fora, o gatilho de volta vai para o rodape.
    /// </summary>
    void SetLogCollapsed(bool recolhido)
    {
        _logRecolhido = recolhido;

        LogPanel.Visibility = _logRecolhido ? Visibility.Collapsed : Visibility.Visible;
        BtnLogShow.Visibility = _logRecolhido ? Visibility.Visible : Visibility.Collapsed;
        LogRow.MinHeight = _logRecolhido ? 0 : AlturaMinimaLog;
        LogRow.Height = _logRecolhido ? new GridLength(0) : new GridLength(0.18, GridUnitType.Star);
    }

    bool _logRecolhido;

    /// <summary>
    /// Piso do console, em pixels. Precisa casar com o MinHeight de LogRow no XAML.
    ///
    /// Eram 96, e 96 nao cabia UMA linha: a moldura vertical do painel soma 94 px
    /// (margem 24 + borda 2 + margem interna 22 + cabecalho 32 + 14 do Padding que o
    /// TxtLog herdava do estilo de TextBox). Sobravam dois pixels. A tira vazia que
    /// aparecia em tela baixa era isto, e nao log vazio.
    /// </summary>
    const int AlturaMinimaLog = 140;

    /// <summary>O console foi aberto pelo trabalho, e não pela pessoa — então volta a fechar.</summary>
    bool _logAbertoPorTrabalho;

    /// <summary>
    /// O log da aplicação: na tela E em arquivo.
    ///
    /// Só na tela ele se perde. A janela fecha, o console é recolhido, ou simplesmente
    /// nada foi registrado — e aí um painel vazio não distingue "não aconteceu nada" de
    /// "aconteceu e ninguém contou". Com o arquivo dá para olhar depois do fato e dá
    /// para mandar para quem for ajudar.
    /// </summary>
    static readonly string ArquivoDeLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IsoForge", "isoforge.log");

    void AppendLog(string message)
    {
        TxtLog.AppendText(message + Environment.NewLine);
        TxtLog.ScrollToEnd();
        GravarNoArquivo(message);
    }

    /// <summary>
    /// Nunca derruba a aplicação por causa do log: disco cheio, pasta sem permissão ou
    /// arquivo em uso são motivos ruins para perder o trabalho de quem está usando.
    /// </summary>
    static void GravarNoArquivo(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ArquivoDeLog)!);
            File.AppendAllText(ArquivoDeLog,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { }
    }
}
