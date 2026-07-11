using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using IsoForge.Core;
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

    public MainWindow()
    {
        InitializeComponent();
        RunVersion.Text = $"v{Updater.CurrentVersion.ToString(3)}";

        // Carrega a configuração salva localmente. Assim os dados preenchidos sobrevivem a
        // atualizações e nada sensível fica no código.
        _config = SettingsStore.Load() ?? _config;

        GridVpn.ItemsSource = _config.VpnTunnels;
        GridUnits.ItemsSource = _config.Units;
        LstDriverCategories.ItemsSource = _driverCategories;
        LstIndividualDrivers.ItemsSource = _individualDrivers;

        ApplyConfigToUi();
        RefreshProfiles();
        RefreshAppCards();
        UpdateUnitPreview();

        // Salva ao fechar — garante que nada preenchido se perca.
        Closing += (_, __) => { try { CollectConfig(); } catch { } };

        // Reflete os apps escolhidos em outras telas.
        _config.Apps.CollectionChanged += (_, __) => Dispatcher.Invoke(() => { UpdateGoldenSummary(); UpdateDynamicConfigCards(); });
        UpdateGoldenSummary();

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
                TxtHeaderStatus.Text = $"Baixando atualização: {p:0}%";
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
            AppendLog($"Verificação de atualização: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Busca automática dos instaladores mais recentes (ao abrir)
    // ------------------------------------------------------------------
    void StartInstallerRefresh()
    {
        TxtHeaderStatus.Text = "Buscando instaladores mais recentes...";
        DownloadBar.Visibility = Visibility.Visible;
        DownloadBar.IsIndeterminate = true;
        var progress = new Progress<string>(AppendLog);
        _ = Task.Run(async () =>
        {
            var ids = new[] { AppId.SevenZip, AppId.AnyDesk, AppId.OfficeOdt };
            foreach (var id in ids)
            {
                var r = await _fetcher.EnsureAsync(id, progress, CancellationToken.None);
                if (r.LocalPath != null) _fetched[id] = r;
            }
            var resumo = string.Join(" · ", _fetched.Values.Select(v => v.Version == "mais recente" ? v.Name : $"{v.Name} {v.Version}"));
            Dispatcher.Invoke(() =>
            {
                TxtHeaderStatus.Text = string.IsNullOrEmpty(resumo) ? "Instaladores: verifique a conexão" : $"Instaladores prontos: {resumo}";
                HideDownloadBar();
            });
        });
    }

    // Progresso em porcentagem no painel de status (texto + barra bem visíveis).
    IProgress<double> PercentTo(string label) =>
        new Progress<double>(pct =>
        {
            TxtHeaderStatus.Text = $"{label}: {pct:0}%";
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
        CardOffice.IsChecked = _config.Apps.Any(a => a.Kind == AppKind.Office);
        CardAnyDesk.IsChecked = _config.Apps.Any(a => a.Name == "AnyDesk");
        CardSevenZip.IsChecked = _config.Apps.Any(a => a.Name == "7-Zip");
        CardForti.IsChecked = _config.Apps.Any(a => a.Name == "FortiClient");
        CardAdobe.IsChecked = _config.Apps.Any(a => a.Name == "Adobe Acrobat Reader");
        CardChrome.IsChecked = _config.Apps.Any(a => a.Name == "Google Chrome");
        CardFirefox.IsChecked = _config.Apps.Any(a => a.Name == "Mozilla Firefox");
        CardNotepad.IsChecked = _config.Apps.Any(a => a.Name == "Notepad++");
        CardVcRedist.IsChecked = _config.Apps.Any(a => a.Name == "Visual C++ 2015-2022 (x64)");
        _syncingCards = false;
        AppsChips.ItemsSource = _config.Apps;
        UpdateDynamicConfigCards();
    }

    void UpdateDynamicConfigCards()
    {
        if (OfficeConfigCard == null) return;
        bool office = _config.Apps.Any(a => a.Kind == AppKind.Office);
        bool forti = _config.Apps.Any(a => a.Name == "FortiClient");
        OfficeConfigCard.Visibility = office ? Visibility.Visible : Visibility.Collapsed;
        FortiConfigCard.Visibility = forti ? Visibility.Visible : Visibility.Collapsed;
        EmptyAppsHint.Visibility = _config.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void CardOffice_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingCards) return;
        if (CardOffice.IsChecked == true)
        {
            var ok = await AddOfficeAsync();
            if (!ok) { _syncingCards = true; CardOffice.IsChecked = false; _syncingCards = false; }
        }
        else
        {
            var app = _config.Apps.FirstOrDefault(a => a.Kind == AppKind.Office);
            if (app != null) _config.Apps.Remove(app);
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
            var dlg = new FortiVersionWindow { Owner = this };
            if (dlg.ShowDialog() != true)
            {
                _syncingCards = true; CardForti.IsChecked = false; _syncingCards = false;
                return;
            }
            _config.FortiClientLatest = dlg.Latest;
            var ok = await AddAutoAsync(dlg.Latest ? AppId.FortiClientLatest : AppId.FortiClient);
            if (!ok) { _syncingCards = true; CardForti.IsChecked = false; _syncingCards = false; }
        }
        else
        {
            var app = _config.Apps.FirstOrDefault(a => a.Name == "FortiClient");
            if (app != null) _config.Apps.Remove(app);
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
            var ok = await AddAutoAsync(id);
            if (!ok) { _syncingCards = true; card.IsChecked = false; _syncingCards = false; }
        }
        else
        {
            var app = _config.Apps.FirstOrDefault(a => a.Name == name);
            if (app != null) _config.Apps.Remove(app);
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
                MessageBox.Show(this, "Não consegui obter o Office Deployment Tool. Verifique a internet.", "IsoForge",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            _fetched[AppId.OfficeOdt] = odt;
            _config.Apps.Add(new AppEntry { Name = odt.Name, InstallerPath = odt.LocalPath, SilentArgs = "", Kind = AppKind.Office });
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
        if (known == null)
        {
            SetBusy(true);
            var progress = new Progress<string>(AppendLog);
            var percent = PercentTo("Baixando");
            try { known = await _fetcher.EnsureAsync(id, progress, CancellationToken.None, percent); }
            finally { SetBusy(false); }
            if (known.LocalPath != null) _fetched[id] = known;
            TxtHeaderStatus.Text = known?.LocalPath != null ? $"{known.Name} pronto" : TxtHeaderStatus.Text;
        }

        if (known?.LocalPath == null || !File.Exists(known.LocalPath))
        {
            MessageBox.Show(this, "Não consegui baixar o instalador automaticamente. Verifique a internet e tente novamente, ou use \"+ Adicionar outro\".",
                "IsoForge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (_config.Apps.Any(a => a.Name == known.Name)) return true;
        _config.Apps.Add(new AppEntry
        {
            Name = known.Name,
            InstallerPath = known.LocalPath,
            SilentArgs = known.SilentArgs,
            Kind = known.IsOffice ? AppKind.Office : AppKind.Generic,
            RequiresInternet = known.RequiresInternet
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
        UpdateGoldenSummary();
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
    void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl) return; // ignora SelectionChanged de listas/combos internos
        if ((MainTabs.SelectedItem as TabItem)?.Header as string == "Drivers")
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
            MessageBox.Show(this, $"Não achei drivers individuais para {m.Name}. Tente o modo 'Pack completo'.",
                "IsoForge", MessageBoxButton.OK, MessageBoxImage.Information);
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
                MessageBox.Show(this, "Marque ao menos um driver.", "IsoForge", MessageBoxButton.OK, MessageBoxImage.Information);
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
                MessageBox.Show(this, $"Falha ao baixar/extrair os drivers.\n\n{ex.Message}", "IsoForge", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { SetBusy(false); }
            return;
        }

        if (LstDriverModels.SelectedItem is not DriverPackModel model)
        {
            MessageBox.Show(this, "Selecione um modelo na lista primeiro.", "IsoForge", MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show(this, $"Falha ao baixar/extrair o driver.\n\n{ex.Message}", "IsoForge", MessageBoxButton.OK, MessageBoxImage.Warning);
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
    void BrowseSourceIso_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Imagem ISO|*.iso", Title = "Selecione a ISO do Windows 11" };
        if (dlg.ShowDialog() != true) return;
        TxtSourceIso.Text = dlg.FileName;
        if (string.IsNullOrWhiteSpace(TxtOutputIso.Text))
        {
            var dir = Path.GetDirectoryName(dlg.FileName)!;
            var name = Path.GetFileNameWithoutExtension(dlg.FileName);
            TxtOutputIso.Text = Path.Combine(dir, $"{name}_PERSONALIZADA.iso");
        }
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
                MessageBox.Show(this,
                    $"Não encontrei o FortiClient instalado/configurado neste computador (chave {key}).\n\n" +
                    "Instale o FortiClient aqui, configure os túneis uma vez e clique novamente — ou use \"Procurar...\" para apontar um .reg exportado em outra máquina.",
                    "IsoForge — FortiClient", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            TxtFortiReg.Text = outFile;
            AppendLog($"Config do FortiClient capturada deste PC: {outFile}");
            MessageBox.Show(this,
                "Configuração do FortiClient capturada deste computador (inclui gateway + PSK cifrada).\n\n" +
                "Ela será importada exatamente como está no 1º logon da ISO — os túneis vão preencher completos (nome, gateway e senha).",
                "IsoForge — FortiClient", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO ao capturar config do FortiClient: {ex.Message}");
            MessageBox.Show(this, ex.Message, "IsoForge — erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void UpdateGoldenSummary()
    {
        if (TxtGoldenSummary == null) return;
        bool offline = RbOfficeOffline?.IsChecked == true;
        var hasOffice = _config.Apps.Any(a => a.Kind == AppKind.Office);
        if (_config.Apps.Count == 0)
        {
            TxtGoldenSummary.Text = "Nenhum aplicativo escolhido ainda — vá à aba Aplicativos e adicione o que quer na imagem.";
            TxtGoldenOfficeNote.Text = "";
            return;
        }
        var names = _config.Apps.Select(a => a.Kind == AppKind.Office
            ? $"Office 365 ({(offline ? "offline" : "online")})"
            : a.Name);
        TxtGoldenSummary.Text = "• " + string.Join("   • ", names);

        TxtGoldenOfficeNote.Text = hasOffice
            ? (offline
                ? "Office: será instalado a partir da fonte offline embutida (sem internet na VM)."
                : "Office: a VM baixará o Office da internet durante a geração. Marque 'Offline' nas opções do Office para evitar isso.")
            : "";
    }

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
            MessageBox.Show(this, "Selecione o Office 365 nos cards primeiro.", "IsoForge",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new OpenFolderDialog { Title = "Escolha onde baixar o Office (a pasta receberá setup.exe + Office\\Data)" };
        if (dlg.ShowDialog() != true) return;

        var cfgXml = _config.OfficeConfigXml;
        var folder = dlg.FolderName;
        SetBusy(true);
        var progress = new Progress<string>(AppendLog);
        var headline = new Progress<string>(s => TxtHeaderStatus.Text = $"Baixando Office offline: {s}");
        try
        {
            AppendLog("==== Baixando Office offline ====");
            await Task.Run(() => OfficeDownloader.DownloadAsync(odt, cfgXml, folder, progress, CancellationToken.None, headline));
            TxtHeaderStatus.Text = "Office offline pronto";
            TxtOfficeSource.Text = folder;
            RbOfficeOffline.IsChecked = true;
            MessageBox.Show(this, "Office baixado. O modo offline foi ativado.", "IsoForge", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO: {ex.Message}");
            MessageBox.Show(this, ex.Message, "IsoForge — erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    void Golden_Changed(object sender, RoutedEventArgs e)
    {
        if (PanelGolden == null) return;
        PanelGolden.IsEnabled = ChkGolden.IsChecked == true;
    }

    void BrowseGoldenWim_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Imagem do Windows|*.wim", Title = "Selecione o install.wim capturado" };
        if (dlg.ShowDialog() == true) TxtGoldenWim.Text = dlg.FileName;
    }

    void SaveGoldenScripts_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Escolha a pasta para salvar os scripts do fluxo golden" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(Path.Combine(dlg.FolderName, "Sysprep-Generalize.cmd"), GoldenImageScripts.Sysprep, System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(dlg.FolderName, "Capture-GoldenImage.ps1"), GoldenImageScripts.Capture, System.Text.Encoding.UTF8);
        File.WriteAllText(Path.Combine(dlg.FolderName, "LEIA-imagem-golden.txt"), GoldenImageScripts.Readme, System.Text.Encoding.UTF8);
        AppendLog($"Scripts do fluxo golden salvos em: {dlg.FolderName}");
        MessageBox.Show(this, "Scripts salvos:\n- Sysprep-Generalize.cmd\n- Capture-GoldenImage.ps1\n- LEIA-imagem-golden.txt", "IsoForge",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    async void GoldenAuto_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectConfig();

        if (!GoldenAutoBuilder.IsAdministrator())
        {
            MessageBox.Show(this,
                "Para gerar a imagem golden automaticamente, feche e reabra o IsoForge como Administrador (botão direito → Executar como administrador). O Hyper-V, o Mount-VHD e o DISM exigem elevação.",
                "IsoForge — precisa de Administrador", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(cfg.SourceIsoPath) || !File.Exists(cfg.SourceIsoPath) ||
            string.IsNullOrWhiteSpace(cfg.OutputIsoPath) ||
            string.IsNullOrWhiteSpace(cfg.OscdimgPath) || !File.Exists(cfg.OscdimgPath) ||
            string.IsNullOrWhiteSpace(cfg.UserName))
        {
            MessageBox.Show(this, "Antes de gerar a imagem golden, preencha: ISO de origem, ISO de saída, oscdimg e o usuário local (aba Sistema e usuário).",
                "IsoForge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            "Isso vai criar uma VM Hyper-V temporária, instalar tudo, capturar a imagem e gerar a ISO golden.\n\n" +
            "Pode levar 30–60 minutos e usar ~40 GB de disco. Deseja continuar?",
            "IsoForge — imagem golden automática", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(AppendLog);
        try
        {
            AppendLog("==== Iniciando geração automática de imagem golden ====");
            await Task.Run(() => new GoldenAutoBuilder(progress).BuildAsync(cfg, _cts.Token));
            MessageBox.Show(this, $"Imagem golden gerada:\n{cfg.OutputIsoPath}", "IsoForge",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO: {ex.Message}");
            MessageBox.Show(this, ex.Message, "IsoForge — erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

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

        _config.UseCapturedWim = ChkGolden.IsChecked == true;
        _config.CapturedWimPath = TxtGoldenWim.Text.Trim();
        _config.WallpaperPath = TxtWallpaper.Text.Trim();
        _config.LockScreenPath = TxtLockScreen.Text.Trim();
        _config.WindowsTheme = RbThemeLight.IsChecked == true ? WindowsThemeMode.Light
            : RbThemeDark.IsChecked == true ? WindowsThemeMode.Dark
            : WindowsThemeMode.Default;
        _config.TaskbarAlign = RbTaskbarCenter.IsChecked == true ? TaskbarAlignment.Center
            : RbTaskbarLeft.IsChecked == true ? TaskbarAlignment.Left
            : TaskbarAlignment.Default;
        _config.DebloatRemoveApps = ChkDebloatApps.IsChecked == true;
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

        ChkGolden.IsChecked = _config.UseCapturedWim;
        TxtGoldenWim.Text = _config.CapturedWimPath;

        TxtWallpaper.Text = _config.WallpaperPath;
        TxtLockScreen.Text = _config.LockScreenPath;
        RbThemeLight.IsChecked = _config.WindowsTheme == WindowsThemeMode.Light;
        RbThemeDark.IsChecked = _config.WindowsTheme == WindowsThemeMode.Dark;
        RbThemeDefault.IsChecked = _config.WindowsTheme == WindowsThemeMode.Default;
        RbTaskbarCenter.IsChecked = _config.TaskbarAlign == TaskbarAlignment.Center;
        RbTaskbarLeft.IsChecked = _config.TaskbarAlign == TaskbarAlignment.Left;
        RbTaskbarDefault.IsChecked = _config.TaskbarAlign == TaskbarAlignment.Default;
        ChkDebloatApps.IsChecked = _config.DebloatRemoveApps;
        ChkDebloatCopilot.IsChecked = _config.DebloatDisableCopilot;
        ChkDebloatTeams.IsChecked = _config.DebloatRemoveTeamsChat;
        ChkDebloatOneDrive.IsChecked = _config.DebloatRemoveOneDrive;
        ChkDebloatAds.IsChecked = _config.DebloatDisableStartAds;
        ChkDebloatTelemetry.IsChecked = _config.DebloatDisableTelemetry;
        ChkReport.IsChecked = _config.GenerateReport;
        ThemeService.Apply(_config.AppDarkTheme);
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
        _config.Apps.CollectionChanged += (_, __) => Dispatcher.Invoke(() => { UpdateGoldenSummary(); UpdateDynamicConfigCards(); });
        ApplyConfigToUi();
        RefreshAppCards();
        UpdateUnitPreview();
        UpdateGoldenSummary();
        AppendLog($"Perfil carregado: {name}");
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
        if (MessageBox.Show(this, $"Excluir o perfil \"{name}\"?", "IsoForge",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
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

    async void Build_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectConfig();
        SetBusy(true);
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(AppendLog);
        var percent = new Progress<int>(p => Dispatcher.Invoke(() => BuildProgress.Value = p));
        BuildProgress.Value = 0;
        BuildProgressPanel.Visibility = Visibility.Visible;
        try
        {
            AppendLog("==== Iniciando geração da ISO ====");
            await Task.Run(() => new IsoPipeline(progress, percent).BuildAsync(cfg, _cts.Token));
            MessageBox.Show(this, $"ISO gerada com sucesso:\n{cfg.OutputIsoPath}", "IsoForge",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO: {ex.Message}");
            MessageBox.Show(this, ex.Message, "IsoForge — erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            BuildProgressPanel.Visibility = Visibility.Collapsed;
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
            new IsoPipeline(progress).DryRun(cfg, dlg.FolderName);
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO: {ex.Message}");
            MessageBox.Show(this, ex.Message, "IsoForge — erro", MessageBoxButton.OK, MessageBoxImage.Error);
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
            var r = MessageBox.Show(this,
                "O Office no modo ONLINE baixa da internet via BITS, que o Windows Sandbox não suporta " +
                "bem (dá o erro 30183 \"couldn't install / download a required file\"), mesmo com internet.\n\n" +
                "Isso é uma limitação do Sandbox — numa máquina real ou VM Hyper-V o Office online instala normal.\n\n" +
                "Para testar o Office, prefira:\n" +
                "  • Office OFFLINE (embute o Office na ISO) — funciona no Sandbox; ou\n" +
                "  • o botão \"Script Hyper-V\" (testa o fluxo online numa VM de verdade).\n\n" +
                "Deseja continuar o teste no Sandbox mesmo assim? (os outros apps instalam normalmente)",
                "IsoForge — Office online no Sandbox", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "IsoForge", "SandboxTest");
        var progress = new Progress<string>(AppendLog);
        SetBusy(true);
        try
        {
            AppendLog("==== Preparando teste no Windows Sandbox ====");
            if (Directory.Exists(folder)) { try { Directory.Delete(folder, true); } catch { } }
            string wsb = "";
            await Task.Run(() => wsb = new IsoPipeline(progress).PrepareSandbox(cfg, folder));

            if (!IsoPipeline.IsSandboxAvailable())
            {
                AppendLog($"Windows Sandbox não habilitado. Arquivos de teste em: {folder}");
                MessageBox.Show(this,
                    "O Windows Sandbox não está habilitado nesta máquina.\n\n" +
                    "Habilite (PowerShell como Administrador e reinicie):\n" +
                    "Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All\n\n" +
                    $"Os arquivos de teste já foram gerados em:\n{folder}\n\n" +
                    "Depois de habilitar, dê dois cliques em Testar-Sandbox.wsb.",
                    "IsoForge — Windows Sandbox", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AppendLog("Abrindo o Windows Sandbox — instala os apps numa cópia descartável (igual à VM).");
            AppendLog("Dentro do Sandbox: a janela do install.cmd abre sozinha; ao terminar, veja C:\\Setup\\install.log.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(wsb) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO: {ex.Message}");
            MessageBox.Show(this, ex.Message, "IsoForge — erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    void SaveTestScript_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Script PowerShell|*.ps1",
            FileName = "Testar-HyperV.ps1",
            Title = "Salvar script de teste do Hyper-V"
        };
        if (dlg.ShowDialog() != true) return;

        File.WriteAllText(dlg.FileName, TestScripts.HyperV, System.Text.Encoding.UTF8);
        AppendLog($"Script de teste salvo: {dlg.FileName}");
        AppendLog("Execute em um PowerShell como Administrador: .\\Testar-HyperV.ps1 -IsoPath \"caminho\\da\\ISO.iso\"");
    }

    void SetBusy(bool busy)
    {
        BtnBuild.IsEnabled = !busy;
        BtnDryRun.IsEnabled = !busy;
        BtnTestScript.IsEnabled = !busy;
        if (BtnSandbox != null) BtnSandbox.IsEnabled = !busy;
        if (!busy) HideDownloadBar();
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;
    }

    void AppendLog(string message)
    {
        TxtLog.AppendText(message + Environment.NewLine);
        TxtLog.ScrollToEnd();
    }
}
