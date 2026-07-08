using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using IsoForge.Core;
using IsoForge.Models;
using Microsoft.Win32;

namespace IsoForge;

public partial class MainWindow : Window
{
    BuildConfig _config = new();
    readonly InstallerFetcher _fetcher = new();
    readonly ConcurrentDictionary<AppId, FetchResult> _fetched = new();
    CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();

        // Carrega a configuração salva localmente (%APPDATA%\IsoForge\settings.json), se houver.
        // Assim os dados preenchidos sobrevivem a atualizações e nada sensível fica no código.
        _config = SettingsStore.Load() ?? _config;

        GridApps.ItemsSource = _config.Apps;
        GridVpn.ItemsSource = _config.VpnTunnels;
        GridUnits.ItemsSource = _config.Units;
        TxtOfficeConfig.Text = string.IsNullOrWhiteSpace(_config.OfficeConfigXml)
            ? BuildConfig.DefaultOfficeConfig : _config.OfficeConfigXml;

        ApplyConfigToUi();
        RefreshProfiles();

        // Salva ao fechar (CollectConfig já persiste) — garante que nada preenchido se perca.
        Closing += (_, __) => { try { CollectConfig(); } catch { } };

        // Resumo da imagem golden reflete os apps escolhidos
        _config.Apps.CollectionChanged += (_, __) => Dispatcher.Invoke(UpdateGoldenSummary);
        UpdateGoldenSummary();

        var bundled = Oscdimg.ExtractBundled();
        if (bundled != null)
        {
            TxtOscdimg.Text = bundled;
            AppendLog("oscdimg.exe embutido no IsoForge — nada para baixar ou instalar.");
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

        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--updated", StringComparison.OrdinalIgnoreCase)))
        {
            // Reaberto pelo instalador após um auto-update silencioso.
            var v = Updater.CurrentVersion.ToString(3);
            TxtHeaderStatus.Text = $"✔ Atualizado com sucesso para a versão {v}";
            AppendLog($"✔ Atualizado com sucesso para a versão {v}.");
            Loaded += (_, __) => MessageBox.Show(this,
                $"Atualizado com sucesso para a versão {v}!", "IsoForge",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            _ = CheckForUpdateAsync();
        }
    }

    // ------------------------------------------------------------------
    // Auto-atualização: checa o último release no GitHub ao abrir.
    // ------------------------------------------------------------------
    async Task CheckForUpdateAsync()
    {
        try
        {
            var info = await Updater.CheckAsync();
            if (info == null || Updater.IsSkipped(info.Version)) return; // atualizado / pulado / sem internet

            var dlg = new UpdateWindow(info.Version, Updater.CurrentVersion, info.Notes) { Owner = this };
            dlg.ShowDialog();
            if (dlg.Choice == UpdateChoice.Skip) { Updater.SkipVersion(info.Version); return; }
            if (dlg.Choice != UpdateChoice.Update) return; // "Depois"

            var pct = new Progress<double>(p => Dispatcher.Invoke(() => TxtHeaderStatus.Text = $"Baixando atualização: {p:0}%"));
            var path = await Updater.DownloadAsync(info, pct);
            CollectConfig(); // salva a config antes de sair
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
        var progress = new Progress<string>(AppendLog);
        _ = Task.Run(async () =>
        {
            // Apps leves ao abrir; o Adobe (~700 MB) é baixado sob demanda ao adicioná-lo.
            var ids = new[] { AppId.SevenZip, AppId.AnyDesk, AppId.OfficeOdt };
            foreach (var id in ids)
            {
                var r = await _fetcher.EnsureAsync(id, progress, CancellationToken.None);
                if (r.LocalPath != null) _fetched[id] = r;
            }
            var resumo = string.Join(" · ", _fetched.Values.Select(v => v.Version == "mais recente" ? v.Name : $"{v.Name} {v.Version}"));
            Dispatcher.Invoke(() => TxtHeaderStatus.Text = string.IsNullOrEmpty(resumo) ? "Instaladores: verifique a conexão" : $"Instaladores prontos: {resumo}");
        });
    }

    // Progresso em porcentagem mostrado no cabeçalho (atualiza no lugar, sem poluir o log).
    IProgress<double> PercentTo(string label) =>
        new Progress<double>(pct => TxtHeaderStatus.Text = $"{label}: {pct:0}%");

    async Task AddAutoAsync(AppId id)
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
            MessageBox.Show(this, $"Não consegui baixar o instalador de {id} automaticamente. Verifique a internet e tente novamente, ou use \"+ Outro...\".",
                "IsoForge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_config.Apps.Any(a => a.Name == known.Name))
        {
            MessageBox.Show(this, $"\"{known.Name}\" já está na lista.", "IsoForge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _config.Apps.Add(new AppEntry
        {
            Name = known.Name,
            InstallerPath = known.LocalPath,
            SilentArgs = known.SilentArgs,
            Kind = known.IsOffice ? AppKind.Office : AppKind.Generic
        });
        AppendLog($"{known.Name} adicionado (versão {known.Version}): {known.LocalPath}");
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
        if (PanelXauthCreds == null) return; // durante InitializeComponent
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

    void OfficeOffline_Changed(object sender, RoutedEventArgs e)
    {
        if (PanelOfficeSource == null) return;
        PanelOfficeSource.IsEnabled = ChkOfficeOffline.IsChecked == true;
        UpdateGoldenSummary();
    }

    void UpdateGoldenSummary()
    {
        if (TxtGoldenSummary == null) return;
        var hasOffice = _config.Apps.Any(a => a.Kind == AppKind.Office);
        if (_config.Apps.Count == 0)
        {
            TxtGoldenSummary.Text = "Nenhum aplicativo escolhido ainda — vá à aba Aplicativos e adicione o que quer na imagem.";
            TxtGoldenOfficeNote.Text = "";
            return;
        }
        var names = _config.Apps.Select(a => a.Kind == AppKind.Office
            ? $"Office 365 ({(ChkOfficeOffline.IsChecked == true ? "offline" : "online")})"
            : a.Name);
        TxtGoldenSummary.Text = "• " + string.Join("   • ", names);

        TxtGoldenOfficeNote.Text = hasOffice
            ? (ChkOfficeOffline.IsChecked == true
                ? "Office: será instalado a partir da fonte offline embutida (sem internet na VM)."
                : "Office: a VM baixará o Office da internet durante a geração. Marque 'Office offline' na aba Aplicativos para evitar isso.")
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
            MessageBox.Show(this, "Adicione o Office na seção 4 primeiro (o setup.exe do ODT).", "IsoForge",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new OpenFolderDialog { Title = "Escolha onde baixar o Office (a pasta receberá setup.exe + Office\\Data)" };
        if (dlg.ShowDialog() != true) return;

        // Ler propriedades da UI ANTES do Task.Run (evita acesso cross-thread).
        var cfgXml = TxtOfficeConfig.Text;
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
            ChkOfficeOffline.IsChecked = true;
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
            MessageBox.Show(this, "Antes de gerar a imagem golden, preencha: ISO de origem, ISO de saída, oscdimg e o usuário local (seção 2).",
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

    void UnitSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (GridUnits == null) return;
        GridUnits.IsEnabled = ChkUnitSelection.IsChecked == true;
        if (PanelUnitMethod != null) PanelUnitMethod.IsEnabled = ChkUnitSelection.IsChecked == true;
    }

    void Mode_Changed(object sender, RoutedEventArgs e)
    {
        // Checked dispara durante o InitializeComponent, antes dos controles abaixo existirem.
        if (ChkAutoLogon == null || ChkUnitSelection == null) return;
        bool entra = RbModeEntra.IsChecked == true;

        // Logon automático não se aplica ao modo Entra ID (não há autologon local).
        ChkAutoLogon.IsEnabled = !entra;
        if (ChkDemoteEntra != null) ChkDemoteEntra.IsEnabled = entra;

        // Seleção de unidade funciona nos dois modos. No Entra ID, porém, o modo de auditoria
        // não se aplica (o nome é aplicado por tarefa no 1º logon): força "1º logon".
        if (RbUnitAudit != null && RbUnitFirstLogon != null)
        {
            RbUnitAudit.IsEnabled = !entra;
            if (entra) RbUnitFirstLogon.IsChecked = true;
        }
    }

    void RemoveUnit_Click(object sender, RoutedEventArgs e)
    {
        if (GridUnits.SelectedItem is UnitEntry u) _config.Units.Remove(u);
    }

    void UnattendMode_Changed(object sender, RoutedEventArgs e)
    {
        if (TxtCustomUnattend == null) return; // durante InitializeComponent
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

    // ------------------------------------------------------------------
    // Aplicativos
    // ------------------------------------------------------------------
    async void AddOffice_Click(object sender, RoutedEventArgs e)
    {
        if (_config.Apps.Any(a => a.Kind == AppKind.Office))
        {
            MessageBox.Show(this, "O Office 365 já está na lista.", "IsoForge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var choice = new OfficeChoiceWindow { Owner = this };
        if (choice.ShowDialog() != true) return;

        SetBusy(true);
        var progress = new Progress<string>(AppendLog);
        try
        {
            // Garante o Office Deployment Tool (setup.exe) baixado.
            var odt = await _fetcher.EnsureAsync(AppId.OfficeOdt, progress, CancellationToken.None);
            if (odt.LocalPath == null || !File.Exists(odt.LocalPath))
            {
                MessageBox.Show(this, "Não consegui obter o Office Deployment Tool. Verifique a internet.", "IsoForge",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _fetched[AppId.OfficeOdt] = odt;

            if (choice.Choice == OfficeChoice.Offline)
            {
                // Baixa a fonte offline para a pasta padrão do programa (temporária até gerar a ISO).
                var target = Path.Combine(_fetcher.BaseFolder, "OfficeData");
                var cfgXml = TxtOfficeConfig.Text; // ler na thread da UI
                var headline = new Progress<string>(s => TxtHeaderStatus.Text = $"Baixando Office offline: {s}");
                AppendLog("==== Baixando Office offline (~3,5 GB) ====");
                await Task.Run(() => OfficeDownloader.DownloadAsync(odt.LocalPath, cfgXml, target, progress, CancellationToken.None, headline));
                TxtHeaderStatus.Text = "Office offline pronto";
                _config.OfficeOffline = true;
                _config.OfficeSourceFolder = target;
                ChkOfficeOffline.IsChecked = true;
                TxtOfficeSource.Text = target;
                AppendLog($"Office offline pronto em: {target}");
            }
            else
            {
                _config.OfficeOffline = false;
                ChkOfficeOffline.IsChecked = false;
                AppendLog("Office 365 no modo online: cada máquina baixará o Office no primeiro logon.");
            }

            _config.Apps.Add(new AppEntry { Name = odt.Name, InstallerPath = odt.LocalPath, SilentArgs = "", Kind = AppKind.Office });
            UpdateGoldenSummary();
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO: {ex.Message}");
            MessageBox.Show(this, ex.Message, "IsoForge — erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    async void AddAnyDesk_Click(object sender, RoutedEventArgs e) => await AddAutoAsync(AppId.AnyDesk);
    async void AddSevenZip_Click(object sender, RoutedEventArgs e) => await AddAutoAsync(AppId.SevenZip);
    async void AddAdobeReader_Click(object sender, RoutedEventArgs e) => await AddAutoAsync(AppId.AdobeReader);
    async void AddFortiClient_Click(object sender, RoutedEventArgs e) => await AddAutoAsync(AppId.FortiClient);

    void AddPreset(AppPreset preset)
    {
        if (_config.Apps.Any(a => a.Name == preset.Name))
        {
            MessageBox.Show(this, $"\"{preset.Name}\" já está na lista.", "IsoForge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new OpenFileDialog { Filter = preset.FileFilter, Title = $"Selecione o instalador — {preset.Name}" };
        MessageBoxHint(preset.Hint);
        if (dlg.ShowDialog() != true) return;

        _config.Apps.Add(new AppEntry
        {
            Name = preset.Name,
            InstallerPath = dlg.FileName,
            SilentArgs = AppPresets.DefaultArgsFor(preset, dlg.FileName),
            Kind = preset.Kind
        });
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

    void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (GridApps.SelectedItem is AppEntry entry)
            _config.Apps.Remove(entry);
    }

    void MessageBoxHint(string hint)
    {
        AppendLog($"Dica: {hint}");
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
        // Auditoria não se aplica ao Entra ID (lá o nome é aplicado por tarefa no 1º logon).
        _config.UnitMethod = (RbUnitAudit.IsChecked == true && _config.Mode != DeploymentMode.EntraId)
            ? UnitSelectionMethod.Audit
            : UnitSelectionMethod.FirstLogon;
        _config.OfficeOffline = ChkOfficeOffline.IsChecked == true;
        _config.OfficeSourceFolder = TxtOfficeSource.Text.Trim();
        _config.UseCapturedWim = ChkGolden.IsChecked == true;
        _config.CapturedWimPath = TxtGoldenWim.Text.Trim();
        _config.WallpaperPath = TxtWallpaper.Text.Trim();
        _config.LockScreenPath = TxtLockScreen.Text.Trim();
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
        _config.OfficeConfigXml = TxtOfficeConfig.Text;

        // Persiste localmente (%APPDATA%\IsoForge\settings.json) a cada coleta.
        SettingsStore.Save(_config);
        return _config;
    }

    /// <summary>Preenche a interface a partir da configuração carregada (inverso do CollectConfig).</summary>
    void ApplyConfigToUi()
    {
        TxtSourceIso.Text = _config.SourceIsoPath;
        TxtOutputIso.Text = _config.OutputIsoPath;
        // OscdimgPath: mantém o detectado nesta execução (não restaura o caminho temporário salvo).
        ChkNoPrompt.IsChecked = _config.NoPromptBoot;
        ChkAutoDisk.IsChecked = _config.AutoSelectDisk;
        ChkSkipWifi.IsChecked = _config.SkipWifiSetup;

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

        ChkOfficeOffline.IsChecked = _config.OfficeOffline;
        TxtOfficeSource.Text = _config.OfficeSourceFolder;
        ChkGolden.IsChecked = _config.UseCapturedWim;
        TxtGoldenWim.Text = _config.CapturedWimPath;

        TxtWallpaper.Text = _config.WallpaperPath;
        TxtLockScreen.Text = _config.LockScreenPath;
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
        GridApps.ItemsSource = _config.Apps;
        GridVpn.ItemsSource = _config.VpnTunnels;
        GridUnits.ItemsSource = _config.Units;
        TxtOfficeConfig.Text = string.IsNullOrWhiteSpace(_config.OfficeConfigXml)
            ? BuildConfig.DefaultOfficeConfig : _config.OfficeConfigXml;
        ApplyConfigToUi();
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
        BuildProgress.Visibility = Visibility.Visible;
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
            BuildProgress.Visibility = Visibility.Collapsed;
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
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;
    }

    void AppendLog(string message)
    {
        TxtLog.AppendText(message + Environment.NewLine);
        TxtLog.ScrollToEnd();
    }
}
