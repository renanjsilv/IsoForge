using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using IsoForge.Core;
using IsoForge.Core.Linux;
using IsoForge.Models;

namespace IsoForge.Linux;

public partial class Janela : Window
{
    readonly BuildConfig _cfg;
    readonly List<CheckBox> _apps = new();
    CancellationTokenSource? _cts;
    bool _carregando = true;

    public Janela()
    {
        InitializeComponent();

        // A configuração é a mesma estrutura do aplicativo do Windows, gravada na mesma
        // pasta (~/.config/IsoForge). Quem usa os dois vê os mesmos campos preenchidos.
        _cfg = SettingsStore.Load() ?? new BuildConfig();
        if (!OsCatalog.IsLinux(_cfg.Os)) _cfg.Os = TargetOs.Ubuntu;

        TxtVersao.Text = "v" + Versao.Numero;

        MontarDistros();
        MontarApps();
        ParaTela();
        _carregando = false;

        Navegacao.SelectionChanged += (_, _) => TrocarPagina();
        ListaDistros.SelectionChanged += (_, _) => TrocarDistro();
        ComboDisco.SelectionChanged += (_, _) => AtualizarLuks();
        OpSeed.IsCheckedChanged += (_, _) => DescreverEntrega();
        OpRepack.IsCheckedChanged += (_, _) => DescreverEntrega();
        ChkSenhaVisivel.IsCheckedChanged += (_, _) =>
            CampoSenha.PasswordChar = ChkSenhaVisivel.IsChecked == true ? '\0' : '●';

        BtProcurarOrigem.Click += async (_, _) => await EscolherIsoOrigem();
        BtProcurarSaida.Click += async (_, _) => await EscolherIsoSaida();
        BtPapel.Click += async (_, _) => await EscolherImagem(CampoPapel);
        BtBloqueio.Click += async (_, _) => await EscolherImagem(CampoBloqueio);
        BtScript.Click += async (_, _) => await EscolherScript();
        BtSalvarPerfil.Click += (_, _) => SalvarConfiguracao();
        BtGerar.Click += async (_, _) => await Gerar();
        BtArquivos.Click += async (_, _) => await SomenteArquivos();
        BtPendrive.Click += async (_, _) => await GravarPendrive();
        BtCancelar.Click += (_, _) => _cts?.Cancel();

        Escrever($"IsoForge {Versao.Numero} — {Ambiente()}");
        AvisarFerramentas();

        // Abre direto numa página. Existe para as capturas de tela do README serem tiradas
        // da janela de verdade, e não de uma maquete: ISOFORGE_PAGINA=3 abre em Aplicativos.
        if (int.TryParse(Environment.GetEnvironmentVariable("ISOFORGE_PAGINA"), out var pagina)
            && pagina >= 0 && pagina < Navegacao.ItemCount)
            Navegacao.SelectedIndex = pagina;
    }

    // ==================================================================
    // Montagem da tela
    // ==================================================================

    void MontarDistros()
    {
        ListaDistros.ItemsSource = OsCatalog.All.Where(o => o.IsLinux).ToList();
        ListaDistros.SelectedIndex = Math.Max(0,
            OsCatalog.All.Where(o => o.IsLinux).ToList().FindIndex(o => o.Id == _cfg.Os));
    }

    void MontarApps()
    {
        ListaApps.ItemsSource = null;
        _apps.Clear();
        var itens = new List<Control>();

        foreach (var id in LinuxAppCatalog.Supported)
        {
            var receita = LinuxAppCatalog.Recipe(id, _cfg);
            var chk = new CheckBox
            {
                Content = receita is { Installable: true } ? receita.DisplayName : LinuxAppCatalog.WindowsName(id),
                Tag = id,
                Width = 250,
                Margin = new Avalonia.Thickness(0, 0, 12, 4),
                IsEnabled = receita is { Installable: true },
                IsChecked = _cfg.Apps.Any(a => a.CatalogId == id.ToString())
            };

            var dica = new StringBuilder();
            if (receita == null || !receita.Installable)
                dica.Append("Sem equivalente nesta distribuição.");
            else
            {
                if (!string.Equals(receita.DisplayName, LinuxAppCatalog.WindowsName(id), StringComparison.OrdinalIgnoreCase))
                    dica.Append($"No Windows: {LinuxAppCatalog.WindowsName(id)}. ");
                if (!string.IsNullOrWhiteSpace(receita.Note)) dica.Append(receita.Note);
            }
            if (dica.Length > 0) ToolTip.SetTip(chk, dica.ToString());

            _apps.Add(chk);
            itens.Add(new StackPanel
            {
                Orientation = Orientation.Vertical,
                Width = 250,
                Margin = new Avalonia.Thickness(0, 0, 14, 10),
                Children =
                {
                    chk,
                    new TextBlock
                    {
                        Text = dica.ToString(),
                        Classes = { "apagado" },
                        Margin = new Avalonia.Thickness(26, 0, 0, 0),
                        MaxWidth = 224
                    }
                }
            });
        }

        ListaApps.ItemsSource = itens;
    }

    // ==================================================================
    // Configuração <-> tela
    // ==================================================================

    void ParaTela()
    {
        var l = _cfg.Linux;
        CampoIsoOrigem.Text = _cfg.SourceIsoPath;
        CampoIsoSaida.Text = _cfg.OutputIsoPath;
        OpSeed.IsChecked = l.Delivery == LinuxDeliveryMode.SeedIso;
        OpRepack.IsChecked = l.Delivery != LinuxDeliveryMode.SeedIso;
        ComboDisco.SelectedIndex = (int)l.DiskMode;
        CampoLuks.Text = l.DiskPassword;
        CampoDisco.Text = l.TargetDisk;

        CampoUsuario.Text = _cfg.UserName;
        CampoNomeCompleto.Text = l.FullName;
        CampoSenha.Text = _cfg.Password;
        ChkAutoLogin.IsChecked = l.AutoLogin;
        ChkSemSenhaRoot.IsChecked = l.DisableRootPassword;
        CampoHostname.Text = _cfg.ComputerName;

        CampoFuso.Text = l.Timezone;
        CampoLocale.Text = l.LocaleId;
        CampoTeclado.Text = l.KeyboardLayout;
        CampoVariante.Text = l.KeyboardVariant;
        ChkSsh.IsChecked = l.InstallSshServer;
        CampoChaveSsh.Text = l.SshAuthorizedKey;

        CampoPacotes.Text = l.ExtraPackages;
        ChkFlatpak.IsChecked = l.EnableFlatpak;
        ChkAtualizar.IsChecked = l.UpdateDuringInstall;
        ChkMinima.IsChecked = l.MinimalInstall;
        ChkProprietarios.IsChecked = l.ProprietaryDrivers;

        CampoPapel.Text = _cfg.WallpaperPath;
        CampoBloqueio.Text = _cfg.LockScreenPath;
        ComboDesktop.SelectedIndex = (int)l.Desktop;
        CampoScript.Text = _cfg.PostScriptPath;
        ChkJogos.IsChecked = l.RemoveDefaultGames;
        ChkTelemetria.IsChecked = l.DisableTelemetry;
        ChkSnap.IsChecked = l.RemoveSnap;
        ChkMotd.IsChecked = l.DisableMotdAds;

        ChkWifi.IsChecked = _cfg.AutoConnectWifi;
        CampoSsid.Text = _cfg.WifiSsid;
        CampoSenhaWifi.Text = _cfg.WifiPassword;
        ChkRelatorio.IsChecked = _cfg.GenerateReport;

        AtualizarLuks();
        DescreverEntrega();
        AtualizarTopo();
    }

    void DaTela()
    {
        var l = _cfg.Linux;
        _cfg.SourceIsoPath = CampoIsoOrigem.Text?.Trim() ?? "";
        _cfg.OutputIsoPath = CampoIsoSaida.Text?.Trim() ?? "";
        l.Delivery = OpSeed.IsChecked == true ? LinuxDeliveryMode.SeedIso : LinuxDeliveryMode.Repack;
        l.DiskMode = (LinuxDiskMode)Math.Max(0, ComboDisco.SelectedIndex);
        l.DiskPassword = CampoLuks.Text ?? "";
        l.TargetDisk = CampoDisco.Text?.Trim() ?? "";

        _cfg.UserName = CampoUsuario.Text?.Trim() ?? "";
        l.FullName = CampoNomeCompleto.Text?.Trim() ?? "";
        _cfg.Password = CampoSenha.Text ?? "";
        l.AutoLogin = ChkAutoLogin.IsChecked == true;
        l.DisableRootPassword = ChkSemSenhaRoot.IsChecked == true;
        _cfg.ComputerName = CampoHostname.Text?.Trim() ?? "";

        l.Timezone = CampoFuso.Text?.Trim() ?? "";
        l.LocaleId = CampoLocale.Text?.Trim() ?? "";
        l.KeyboardLayout = CampoTeclado.Text?.Trim() ?? "";
        l.KeyboardVariant = CampoVariante.Text?.Trim() ?? "";
        l.InstallSshServer = ChkSsh.IsChecked == true;
        l.SshAuthorizedKey = CampoChaveSsh.Text?.Trim() ?? "";

        l.ExtraPackages = CampoPacotes.Text?.Trim() ?? "";
        l.EnableFlatpak = ChkFlatpak.IsChecked == true;
        l.UpdateDuringInstall = ChkAtualizar.IsChecked == true;
        l.MinimalInstall = ChkMinima.IsChecked == true;
        l.ProprietaryDrivers = ChkProprietarios.IsChecked == true;

        _cfg.WallpaperPath = CampoPapel.Text?.Trim() ?? "";
        _cfg.LockScreenPath = CampoBloqueio.Text?.Trim() ?? "";
        l.Desktop = (LinuxDesktop)Math.Max(0, ComboDesktop.SelectedIndex);
        _cfg.PostScriptPath = CampoScript.Text?.Trim() ?? "";
        l.RemoveDefaultGames = ChkJogos.IsChecked == true;
        l.DisableTelemetry = ChkTelemetria.IsChecked == true;
        l.RemoveSnap = ChkSnap.IsChecked == true;
        l.DisableMotdAds = ChkMotd.IsChecked == true;

        _cfg.AutoConnectWifi = ChkWifi.IsChecked == true;
        _cfg.WifiSsid = CampoSsid.Text?.Trim() ?? "";
        _cfg.WifiPassword = CampoSenhaWifi.Text ?? "";
        _cfg.GenerateReport = ChkRelatorio.IsChecked == true;

        _cfg.Apps.Clear();
        foreach (var chk in _apps.Where(c => c.IsChecked == true && c.IsEnabled))
        {
            var id = (AppId)chk.Tag!;
            _cfg.Apps.Add(new AppEntry { CatalogId = id.ToString(), Name = LinuxAppCatalog.WindowsName(id) });
        }
    }

    // ==================================================================
    // Reações da interface
    // ==================================================================

    void TrocarPagina()
    {
        Paginas.SelectedIndex = Math.Max(0, Navegacao.SelectedIndex);
        TxtPagina.Text = ((Navegacao.SelectedItem as ListBoxItem)?.Content as TextBlock)?.Text ?? "";
    }

    void TrocarDistro()
    {
        if (ListaDistros.SelectedItem is not OsInfo info) return;
        var mudou = _cfg.Os != info.Id;
        _cfg.Os = info.Id;
        AtualizarTopo();
        DescreverEntrega();
        // O catálogo de pacotes muda com a distro: o mesmo card pode virar outro pacote,
        // ou deixar de existir. Remontar é mais honesto que deixar a tela desatualizada.
        if (mudou && !_carregando) { DaTela(); MontarApps(); }
    }

    void AtualizarTopo()
    {
        var info = OsCatalog.Get(_cfg.Os);
        TxtDistro.Text = info.Name;
        TxtSubtitulo.Text = $"Estúdio de imagens {info.Name}";
        TxtRotuloOrigem.Text = $"ISO do {info.Name}:";
    }

    void AtualizarLuks() =>
        LinhaLuks.IsVisible = ComboDisco.SelectedIndex == (int)LinuxDiskMode.EntireDiskEncrypted;

    void DescreverEntrega()
    {
        var seed = OpSeed.IsChecked == true;
        var suportado = LinuxAnswerFile.SeedSupported(_cfg.Os);
        var nome = OsCatalog.NameOf(_cfg.Os);

        if (!seed)
        {
            TxtEntrega.Text = $"A ISO oficial do {nome} é extraída, recebe o arquivo de resposta " +
                              "e é recompilada com o menu de boot já apontando para ele. Precisa do xorriso instalado.";
            return;
        }

        TxtEntrega.Text = suportado
            ? $"Gera uma ISO pequena com o rótulo {LinuxAnswerFile.SeedLabel(_cfg.Os)}. " +
              $"Dê boot pela ISO oficial do {nome} e anexe esta como segunda unidade de CD/DVD."
            : $"O instalador do {nome} não procura o arquivo de resposta numa segunda mídia — " +
              "a geração vai cair no modo reempacotar sozinha.";
    }

    // ==================================================================
    // Seleção de arquivos
    // ==================================================================

    async Task EscolherIsoOrigem()
    {
        var tipos = new FilePickerFileType("Imagens ISO") { Patterns = new[] { "*.iso" } };
        var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"ISO oficial do {OsCatalog.NameOf(_cfg.Os)}",
            AllowMultiple = false,
            FileTypeFilter = new[] { tipos }
        });
        var caminho = r.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(caminho)) return;

        CampoIsoOrigem.Text = caminho;
        if (string.IsNullOrWhiteSpace(CampoIsoSaida.Text))
        {
            var dir = Path.GetDirectoryName(caminho) ?? "";
            var nome = Path.GetFileNameWithoutExtension(caminho);
            CampoIsoSaida.Text = Path.Combine(dir, nome + "-isoforge.iso");
        }
        var rotulo = IsoTools.RotuloDeIso(caminho);
        Escrever(string.IsNullOrWhiteSpace(rotulo)
            ? $"ISO selecionada: {Path.GetFileName(caminho)}"
            : $"ISO selecionada: {Path.GetFileName(caminho)} (rótulo {rotulo})");
    }

    async Task EscolherIsoSaida()
    {
        var r = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salvar a ISO personalizada como",
            DefaultExtension = "iso",
            SuggestedFileName = $"{OsCatalog.Get(_cfg.Os).ShortName}-isoforge.iso"
        });
        var caminho = r?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(caminho)) CampoIsoSaida.Text = caminho;
    }

    async Task EscolherImagem(TextBox destino)
    {
        var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Escolher imagem",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Imagens") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } }
            }
        });
        var caminho = r.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(caminho)) destino.Text = caminho;
    }

    async Task EscolherScript()
    {
        var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Script pós-instalação",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Shell") { Patterns = new[] { "*.sh", "*" } } }
        });
        var caminho = r.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(caminho)) CampoScript.Text = caminho;
    }

    // ==================================================================
    // Ações
    // ==================================================================

    void SalvarConfiguracao()
    {
        DaTela();
        SettingsStore.Save(_cfg);
        Escrever($"Configuração salva em {SettingsStore.FilePath}.");
    }

    async Task Gerar()
    {
        DaTela();
        var pipeline = new LinuxIsoPipeline(Progresso(), Percentual());

        try { pipeline.Validate(_cfg, dryRun: false); }
        catch (Exception ex) { await Caixa.Erro(this, "Falta alguma coisa", ex.Message); return; }

        var confirma = await Caixa.Perguntar(this, "Gerar a ISO?",
            $"A ISO personalizada do {OsCatalog.NameOf(_cfg.Os)} será gravada em:\n{_cfg.OutputIsoPath}\n\n" +
            "A imagem carrega a senha do usuário (como hash) e, se você preencheu, a do Wi-Fi. " +
            "Trate o arquivo como material sensível.",
            "Gerar", "Cancelar");
        if (!confirma) return;

        SettingsStore.Save(_cfg);
        await Trabalhar("Geração da ISO", ct => pipeline.BuildAsync(_cfg, ct));
    }

    async Task SomenteArquivos()
    {
        DaTela();
        var pipeline = new LinuxIsoPipeline(Progresso());

        try { pipeline.Validate(_cfg, dryRun: true); }
        catch (Exception ex) { await Caixa.Erro(this, "Falta alguma coisa", ex.Message); return; }

        var r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Onde gravar os arquivos gerados",
            AllowMultiple = false
        });
        var pasta = r.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(pasta)) return;

        try
        {
            pipeline.DryRun(_cfg, pasta);
            Escrever($"Arquivos gravados em {pasta}.");
            await Caixa.Informar(this, "Pronto", $"Os arquivos foram gravados em:\n{pasta}");
        }
        catch (Exception ex)
        {
            Escrever("ERRO: " + ex.Message);
            await Caixa.Erro(this, "Não deu certo", ex.Message);
        }
    }

    async Task GravarPendrive()
    {
        DaTela();
        var iso = _cfg.OutputIsoPath;
        if (string.IsNullOrWhiteSpace(iso) || !File.Exists(iso))
        {
            await Caixa.Erro(this, "Sem ISO para gravar",
                "Gere a ISO primeiro, ou aponte em \"Salvar ISO como\" um arquivo que já exista.");
            return;
        }

        var janela = new JanelaPendrive(iso);
        await janela.ShowDialog(this);
        foreach (var linha in janela.Registro) Escrever(linha);
    }

    // ==================================================================
    // Execução com log
    // ==================================================================

    async Task Trabalhar(string titulo, Func<CancellationToken, Task> tarefa)
    {
        _cts = new CancellationTokenSource();
        Ocupado(true);
        Escrever("");
        Escrever($"=== {titulo} ===");

        try
        {
            await Task.Run(() => tarefa(_cts.Token));
            await Caixa.Informar(this, "Pronto", $"{titulo} concluída.\n\n{_cfg.OutputIsoPath}");
        }
        catch (OperationCanceledException)
        {
            Escrever("Cancelado pelo usuário.");
        }
        catch (Exception ex)
        {
            Escrever("ERRO: " + ex.Message);
            await Caixa.Erro(this, "Não deu certo", ex.Message);
        }
        finally
        {
            Ocupado(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    void Ocupado(bool sim)
    {
        BtGerar.IsEnabled = BtArquivos.IsEnabled = BtPendrive.IsEnabled = !sim;
        BtCancelar.IsEnabled = sim;
        Barra.IsVisible = sim;
        Barra.Value = 0;
    }

    IProgress<string> Progresso() => new Progress<string>(Escrever);
    IProgress<int> Percentual() => new Progress<int>(p => Barra.Value = p);

    void Escrever(string linha)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Escrever(linha)); return; }
        Log.Text = string.IsNullOrEmpty(Log.Text) ? linha : Log.Text + "\n" + linha;
        RolagemLog.ScrollToEnd();
    }

    // ==================================================================

    static string Ambiente()
    {
        if (Plataforma.EhLinux)
        {
            var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
            return string.IsNullOrWhiteSpace(desktop) ? "Linux" : $"Linux ({desktop})";
        }
        return Plataforma.EhWindows ? "Windows" : "este sistema";
    }

    /// <summary>
    /// Diz de cara o que falta, em vez de deixar o usuário preencher a tela inteira e só então
    /// descobrir que não há como compilar a ISO.
    /// </summary>
    void AvisarFerramentas()
    {
        var xorriso = IsoTools.FindXorriso();
        if (xorriso == null)
            Escrever("AVISO: xorriso não encontrado — sem ele não há como gerar a ISO. " +
                     "Instale com 'sudo apt install xorriso', 'sudo dnf install xorriso' ou 'sudo pacman -S libisoburn'.");
        else
            Escrever($"xorriso: {xorriso}");

        if (Plataforma.NoCaminho("bsdtar", "7z", "7zz", "7za") == null && xorriso == null)
            Escrever("AVISO: nenhuma ferramenta de extração encontrada (bsdtar, 7z ou xorriso). " +
                     "O modo \"reempacotar\" precisa de uma delas.");
    }
}
