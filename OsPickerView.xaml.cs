using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IsoForge.Models;

namespace IsoForge;

/// <summary>
/// Cores derivadas da marca de um sistema. Existe porque "usar a cor da marca"
/// direto reprova contraste: branco sobre o verde do Mint (#87CF3E) mede 1,90:1,
/// e o azul-marinho do AlmaLinux (#0F4266) some num fundo escuro.
/// Todo derivado aqui é NORMALIZADO por luminância, então o mesmo elemento tem a
/// mesma força visual nas 11 marcas — e não a força que o acaso da marca deu.
/// </summary>
public static class Marca
{
    /// <summary>Luminância relativa (WCAG 2.x).</summary>
    public static double Lum(Color c)
    {
        static double Canal(double v)
        {
            v /= 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Canal(c.R) + 0.7152 * Canal(c.G) + 0.0722 * Canal(c.B);
    }

    /// <summary>Razão de contraste WCAG entre duas cores opacas.</summary>
    public static double Contraste(Color a, Color b)
    {
        double la = Lum(a), lb = Lum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static Color Misturar(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>Escala os três canais pelo mesmo fator: muda o BRILHO sem lavar
    /// o matiz (misturar com branco/preto dessatura — foi assim que a v1 fez
    /// AlmaLinux, Arch, Windows 11 e Fedora virarem o mesmo azul-marinho).</summary>
    public static Color Escalar(Color c, double k) => Color.FromRgb(
        (byte)Math.Clamp(Math.Round(c.R * k), 0, 255),
        (byte)Math.Clamp(Math.Round(c.G * k), 0, 255),
        (byte)Math.Clamp(Math.Round(c.B * k), 0, 255));

    /// <summary>
    /// "Tinta" da marca: escurece até o BRANCO alcançar 4,5:1 em cima dela.
    /// As logos de OsLogos.xaml são vetores brancos e não dá para recolorir,
    /// então quem tem de ceder é o fundo do selo.
    /// </summary>
    public static Color Tinta(Color c)
    {
        var r = c;
        for (var i = 0; i < 60 && Contraste(Colors.White, r) < 4.5; i++)
            r = Escalar(r, 0.94);
        return r;
    }

    /// <summary>
    /// Marca ajustada para ser VISTA sobre o fundo do tema: no claro empurra a
    /// luminância para baixo de 0,30; no escuro, para cima de 0,32 — sempre
    /// escalando os canais, para o matiz sobreviver. É isto que faz o fundo
    /// realmente mudar de cor entre um sistema e outro.
    /// </summary>
    public static Color Legivel(Color c, bool escuro)
    {
        var r = c;
        if (escuro)
        {
            for (var i = 0; i < 60 && Lum(r) < 0.32 && Math.Max(r.R, Math.Max(r.G, r.B)) < 250; i++)
                r = Escalar(r, 1.08);
            // Marca escura E dessaturada (cinza): escalar não chega lá; aí sim clareia.
            for (var i = 0; i < 40 && Lum(r) < 0.32; i++) r = Misturar(r, Colors.White, 0.08);
        }
        else
        {
            for (var i = 0; i < 60 && Lum(r) > 0.30; i++) r = Escalar(r, 0.92);
        }
        return r;
    }

    public static Color Alfa(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

/// <summary>
/// Faixa do tabuleiro. Uma faixa por família — é isso que tira a confusão da
/// parte Linux. A faixa de SUSE e Arch é a única com duas metades
/// (<see cref="Title2"/>/<see cref="Sub2"/>): cada rótulo cai exatamente em cima
/// do seu cartão, porque tanto o cabeçalho quanto a grade dividem a faixa em 2.
/// É um record para o WPF conseguir agrupar por igualdade de valor.
/// </summary>
/// <param name="Ordem">Posição da faixa no tabuleiro (usada de fato na ordenação).</param>
public sealed record OsGroup(int Ordem, string Title, string Sub, string? Title2 = null, string? Sub2 = null);

/// <summary>Item exibido no tabuleiro de escolha do sistema operacional.</summary>
public class OsCardVm
{
    public OsInfo Info { get; }
    public string Name => Info.Name;
    public string Tagline => Info.Tagline;

    /// <summary>Ordem no catálogo — desempata a ordenação dentro da faixa.</summary>
    public int Ordem { get; }

    /// <summary>Logo do sistema (vetor de OsLogos.xaml), desenhada em branco.</summary>
    public ImageSource? Logo { get; }

    /// <summary>Cor de marca pura. Só em traços finos (borda do selo), nunca atrás
    /// de texto branco.</summary>
    public Brush Brush { get; }

    /// <summary>Marca escurecida até o branco alcançar 4,5:1: fundo do selo da
    /// logo e do selo de seleção.</summary>
    public Brush InkBrush { get; }

    /// <summary>Marca normalizada para o tema: anel do cartão selecionado. Força
    /// igual nas 11 marcas.</summary>
    public Brush RingBrush { get; }

    /// <summary>Mesma cor bem diluída: fundo do cartão selecionado.</summary>
    public Brush SoftBrush { get; }

    /// <summary>Halo radial na metade direita do cartão: dá presença de marca e
    /// preenche cartões largos sem chegar perto do texto.</summary>
    public Brush AuraBrush { get; }

    /// <summary>Faixa (família) a que o cartão pertence.</summary>
    public OsGroup Group { get; }

    /// <summary>Mecanismo de automação, por extenso (usado no trilho).</summary>
    public string Mechanism => MechanismOf(Info);

    /// <summary>Versão curta do mecanismo.</summary>
    public string ShortMechanism => Info.Unattend switch
    {
        UnattendKind.WindowsUnattend => "autounattend",
        UnattendKind.CloudInitAutoinstall => "autoinstall",
        UnattendKind.DebianPreseed => "preseed",
        UnattendKind.Kickstart => "kickstart",
        UnattendKind.AutoYast => "AutoYaST",
        _ => "archinstall"
    };

    /// <summary>Linha fixa do cartão: SEMPRE mecanismo · arquivo de resposta,
    /// SEMPRE nesta ordem. O trilho ensina o vocabulário com os rótulos por
    /// extenso; o cartão repete na mesma ordem.</summary>
    public string Meta => $"{ShortMechanism}  ·  {Info.AnswerFileName}";

    public OsCardVm(OsInfo info, int ordem, bool escuro)
    {
        Info = info;
        Ordem = ordem;

        var cor = ToColor(info.Accent);
        var tinta = Marca.Tinta(cor);
        var anel = escuro ? Marca.Legivel(cor, true) : tinta;

        Brush = new SolidColorBrush(cor);
        InkBrush = new SolidColorBrush(tinta);
        RingBrush = new SolidColorBrush(anel);
        SoftBrush = new SolidColorBrush(Marca.Alfa(anel, escuro ? (byte)0x3A : (byte)0x22));
        AuraBrush = new RadialGradientBrush(
            new GradientStopCollection
            {
                new(Marca.Alfa(anel, escuro ? (byte)0x2E : (byte)0x18), 0),
                new(Marca.Alfa(anel, 0x00), 1)
            })
        {
            GradientOrigin = new Point(1.03, 0.5),
            Center = new Point(1.03, 0.5),
            RadiusX = 0.34,
            RadiusY = 0.95
        };

        Group = GroupOf(info);
        Logo = Application.Current?.TryFindResource("Logo." + info.ShortName) as ImageSource;
    }

    public static Color ToColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static string MechanismOf(OsInfo info) => info.Unattend switch
    {
        UnattendKind.WindowsUnattend => "Windows Setup (unattend)",
        UnattendKind.CloudInitAutoinstall => "autoinstall (cloud-init)",
        UnattendKind.DebianPreseed => "preseed",
        UnattendKind.Kickstart => "kickstart (Anaconda)",
        UnattendKind.AutoYast => "AutoYaST",
        _ => "archinstall"
    };

    /// <summary>Nome da família como o usuário lê. É exatamente o rótulo que
    /// aparece em cima do cartão no tabuleiro — o trilho e a faixa nunca podem
    /// discordar.</summary>
    public static string FamilyLabel(OsInfo info) => info.Family switch
    {
        OsFamily.Windows => "Windows",
        OsFamily.Debian => "Debian & Ubuntu",
        OsFamily.RedHat => "Red Hat",
        OsFamily.Suse => "SUSE",
        OsFamily.Arch => "Arch",
        _ => info.Family.ToString()
    };

    static readonly OsGroup FaixaWindows = new(0, "WINDOWS", "pacotes: instaladores silenciosos");
    static readonly OsGroup FaixaDebian = new(1, "DEBIAN & UBUNTU", "pacotes: apt-get");
    static readonly OsGroup FaixaRedHat = new(2, "RED HAT", "pacotes: dnf");
    static readonly OsGroup FaixaSuseArch = new(3, "SUSE", "pacotes: zypper", "ARCH", "pacotes: pacman");

    /// <summary>
    /// Faixa do tabuleiro. SUSE e Arch dividem a MESMA FAIXA (têm uma distro cada),
    /// mas com cabeçalho partido ao meio: cada nome fica exatamente em cima do seu
    /// cartão. Uma família nova cai numa faixa própria — nunca é engolida pela
    /// última, como acontecia com o antigo caso-curinga.
    /// </summary>
    public static OsGroup GroupOf(OsInfo info) => info.Family switch
    {
        OsFamily.Windows => FaixaWindows,
        OsFamily.Debian => FaixaDebian,
        OsFamily.RedHat => FaixaRedHat,
        OsFamily.Suse or OsFamily.Arch => FaixaSuseArch,
        _ => new OsGroup(4, info.Family.ToString().ToUpperInvariant(),
                         "pacotes: " + (string.IsNullOrWhiteSpace(info.PackageManager)
                             ? "instaladores silenciosos" : info.PackageManager))
    };
}

/// <summary>
/// Escolha do sistema operacional. É uma VIEW, não uma janela: vive dentro da
/// MainWindow como uma camada por cima do shell. Antes eram duas janelas — a
/// primeira fechava e a segunda abria, e o app parecia dois programas.
/// A escolha define o pipeline (Windows ou Linux), as abas visíveis e o catálogo de aplicativos.
///
/// Layout: uma superfície só, do mesmo tema, com o fundo animado atrás de tudo;
/// cabeçalho em cima, tabuleiro de faixas no meio (preenche a tela por construção)
/// e o trilho com a ficha técnica embaixo.
/// </summary>
public partial class OsPickerView : UserControl
{
    /// <summary>Período comum de TODAS as animações do fundo, em segundos.
    /// Os períodos individuais (120/60/40 s de rotação e 30/20/15/12 s de deriva
    /// com AutoReverse) dividem este valor, então o conjunto realmente se repete
    /// aqui e PreviewSeek(0) é idêntico a PreviewSeek(1).</summary>
    const double AmbientCiclo = 120;

    /// <summary>Sistema escolhido (válido quando o diálogo devolve true).</summary>
    public TargetOs SelectedOs { get; private set; } = TargetOs.Windows11;

    bool _syncing;
    bool _escuro;
    Storyboard? _ambient;
    List<OsCardVm> _ordem = [];

    // Pincéis animáveis criados em C# (nunca congelados, ao contrário dos que
    // vêm de um ResourceDictionary) — é neles que a cor da marca faz a transição.
    readonly SolidColorBrush _marca = new(Colors.Transparent);   // trilhas do fundo, fio do trilho
    readonly SolidColorBrush _tinta = new(Colors.Transparent);   // selo (fundo de logo branca)
    readonly GradientStop[] _washA = Stops();
    readonly GradientStop[] _washB = Stops();
    readonly TranslateTransform _railShift = new();

    static GradientStop[] Stops() =>
    [
        new(Colors.Transparent, 0),
        new(Colors.Transparent, 0.45),
        new(Colors.Transparent, 1)
    ];

    static RadialGradientBrush Wash(GradientStop[] stops) => new()
    {
        GradientOrigin = new Point(0.5, 0.5),
        Center = new Point(0.5, 0.5),
        RadiusX = 0.5,
        RadiusY = 0.5,
        GradientStops = new GradientStopCollection(stops)
    };

    public OsPickerView(TargetOs current)
    {
        InitializeComponent();

        _escuro = TemaEscuro();

        // O fundo animado lê três pincéis do próprio Control (ver AmbientBackdrop
        // em Theme.xaml): Background e BorderBrush são os washes, Foreground é a
        // cor das trilhas. Montados aqui para poder animar a cor depois.
        Backdrop.Background = Wash(_washA);
        Backdrop.BorderBrush = Wash(_washB);
        Backdrop.Foreground = _marca;

        RailChip.Background = _tinta;
        RailChip.BorderBrush = _marca;
        BrandEdge.Background = _marca;
        RailContent.RenderTransform = _railShift;

        // Uma lista só para os 11 sistemas: as setas do teclado percorrem o
        // catálogo inteiro (com duas listas elas paravam na fronteira).
        var itens = OsCatalog.All.Select((o, i) => new OsCardVm(o, i, _escuro)).ToList();
        var view = new CollectionViewSource { Source = itens };
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(OsCardVm.Group)));
        view.SortDescriptions.Add(new SortDescription("Group.Ordem", ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(OsCardVm.Ordem), ListSortDirection.Ascending));
        LstOs.ItemsSource = view.View;
        _ordem = view.View.Cast<OsCardVm>().ToList();

        SelectedOs = current;
        Preselect(current);
        MostrarFicha(OsCatalog.Get(current), animar: false);
        Densidade(Width, Height);
    }

    static bool TemaEscuro()
    {
        var bg = Application.Current?.TryFindResource("WindowBg") as SolidColorBrush;
        return bg != null && Marca.Lum(bg.Color) < 0.2;
    }

    // ------------------------------------------------------------------ seleção

    /// <summary>Marca um sistema sem disparar evento (o hospedeiro chama ao reabrir).</summary>
    public void Preselect(TargetOs os)
    {
        _syncing = true;
        try { LstOs.SelectedItem = _ordem.FirstOrDefault(v => v.Info.Id == os); }
        finally { _syncing = false; }
    }

    void Os_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (LstOs.SelectedItem is not OsCardVm vm) return;

        SelectedOs = vm.Info.Id;
        MostrarFicha(vm.Info, animar: true);
    }

    void Os_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstOs.SelectedItem is not OsCardVm) return;
        // Só confirma se o duplo clique caiu num cartão — não num rótulo de faixa
        // nem no vazio da lista.
        if (e.OriginalSource is DependencyObject alvo && Ancestral<ListBoxItem>(alvo) != null)
            Ok_Click(sender, new RoutedEventArgs());
    }

    /// <summary>
    /// Navegação por teclado de verdade: esquerda/direita percorrem o catálogo
    /// inteiro na ordem do tabuleiro e cima/baixo saltam de faixa mantendo a
    /// coluna. O rodapé promete isso — agora é verdade.
    /// </summary>
    void Os_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_ordem.Count == 0) return;
        var atual = Math.Max(0, LstOs.SelectedIndex);
        int destino;

        switch (e.Key)
        {
            case Key.Left: destino = atual - 1; break;
            case Key.Right: destino = atual + 1; break;
            case Key.Home: destino = 0; break;
            case Key.End: destino = _ordem.Count - 1; break;
            case Key.Up:
            case Key.Down:
                destino = PorFaixa(atual, e.Key == Key.Down ? 1 : -1);
                break;
            default: return;
        }

        e.Handled = true;
        Selecionar(Math.Clamp(destino, 0, _ordem.Count - 1));
    }

    /// <summary>Mesma posição relativa, uma faixa acima/abaixo.</summary>
    int PorFaixa(int atual, int passo)
    {
        var faixas = _ordem.Select(v => v.Group).Distinct().OrderBy(g => g.Ordem).ToList();
        var faixa = faixas.IndexOf(_ordem[atual].Group);
        var destinoFaixa = Math.Clamp(faixa + passo, 0, faixas.Count - 1);
        if (destinoFaixa == faixa) return atual;

        var inicioAtual = _ordem.FindIndex(v => v.Group == faixas[faixa]);
        var coluna = atual - inicioAtual;
        var alvo = _ordem.Where(v => v.Group == faixas[destinoFaixa]).ToList();
        var inicioAlvo = _ordem.IndexOf(alvo[0]);
        return inicioAlvo + Math.Min(coluna, alvo.Count - 1);
    }

    void Selecionar(int indice)
    {
        LstOs.SelectedIndex = indice;
        LstOs.ScrollIntoView(LstOs.SelectedItem);
        Foco();
    }

    /// <summary>Leva o foco (e a rolagem) até o cartão selecionado. Sem isto, o
    /// cartão pré-selecionado podia ficar fora da tela, sem barra e sem marca.</summary>
    /// <summary>Rola ate o cartao selecionado e da foco nele (o hospedeiro chama ao reabrir).</summary>
    public void Foco()
    {
        if (LstOs.SelectedItem == null) return;
        LstOs.ScrollIntoView(LstOs.SelectedItem);
        LstOs.UpdateLayout();
        if (LstOs.ItemContainerGenerator.ContainerFromItem(LstOs.SelectedItem) is ListBoxItem c)
            c.Focus();
    }

    static T? Ancestral<T>(DependencyObject no) where T : DependencyObject
    {
        for (var atual = no; atual != null; atual = VisualTreeHelper.GetParent(atual))
            if (atual is T achou) return achou;
        return null;
    }

    /// <summary>O usuário confirmou a escolha (<see cref="SelectedOs"/>).</summary>
    public event EventHandler? Confirmed;

    /// <summary>O usuário desistiu de trocar de sistema.</summary>
    public event EventHandler? Cancelled;

    void Ok_Click(object sender, RoutedEventArgs e) => Confirmed?.Invoke(this, EventArgs.Empty);

    void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);

    // -------------------------------------------------------------------- trilho

    /// <summary>Mostra a ficha do sistema no trilho e leva o fundo para a cor da marca.</summary>
    void MostrarFicha(OsInfo info, bool animar)
    {
        var cor = OsCardVm.ToColor(info.Accent);
        var visivel = Marca.Legivel(cor, _escuro);   // normaliza: toda marca "acende" igual
        var tinta = Marca.Tinta(cor);                // fundo do selo (logo é branca)

        TxtSelected.Text = info.Name;
        TxtHint.Text = $"{OsCardVm.FamilyLabel(info).ToUpperInvariant()}   ·   " +
                       "setas percorrem o catálogo · Enter continua · duplo clique abre direto";
        SpecAuto.Text = OsCardVm.MechanismOf(info);
        SpecFile.Text = info.AnswerFileName;
        SpecPkg.Text = string.IsNullOrWhiteSpace(info.PackageManager)
            ? "instaladores silenciosos"
            : info.PackageManager;

        RailLogo.Source = TryFindResource("Logo." + info.ShortName) as ImageSource
                          ?? Application.Current?.TryFindResource("Logo." + info.ShortName) as ImageSource;

        // No claro os washes são discretos (fundo claro); no escuro precisam de
        // corpo para a marca aparecer.
        var (a0, a1) = _escuro ? ((byte)0xB4, (byte)0x48) : ((byte)0x40, (byte)0x1A);
        var (b0, b1) = _escuro ? ((byte)0x78, (byte)0x30) : ((byte)0x30, (byte)0x14);

        if (animar)
        {
            Tingir(_marca, visivel, 0xFF, 380);
            Tingir(_tinta, tinta, 0xFF, 380);
            Pintar(_washA, visivel, a0, a1, 620);
            Pintar(_washB, visivel, b0, b1, 620);

            RailContent.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(260)));
            _railShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(360))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            _marca.Color = visivel;
            _tinta.Color = tinta;
            Pintar(_washA, visivel, a0, a1, 0);
            Pintar(_washB, visivel, b0, b1, 0);
        }
    }

    static void Pintar(GradientStop[] wash, Color cor, byte centro, byte meio, int ms)
    {
        byte[] alfas = [centro, meio, 0x00];
        for (var i = 0; i < wash.Length; i++)
        {
            if (ms <= 0) wash[i].Color = Marca.Alfa(cor, alfas[i]);
            else Tingir(wash[i], cor, alfas[i], ms);
        }
    }

    static void Tingir(SolidColorBrush alvo, Color cor, byte a, int ms) =>
        alvo.BeginAnimation(SolidColorBrush.ColorProperty, Fade(cor, a, ms));

    static void Tingir(GradientStop alvo, Color cor, byte a, int ms) =>
        alvo.BeginAnimation(GradientStop.ColorProperty, Fade(cor, a, ms));

    static ColorAnimation Fade(Color cor, byte a, int ms) =>
        new(Marca.Alfa(cor, a), TimeSpan.FromMilliseconds(ms))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

    // ------------------------------------------------------------- densidade

    void Root_SizeChanged(object sender, SizeChangedEventArgs e) =>
        Densidade(e.NewSize.Width, e.NewSize.Height);

    /// <summary>
    /// Ajusta a densidade do tabuleiro à área disponível. Sem isto o cartão teria
    /// tamanho fixo e sobraria vazio nas telas grandes — o pecado da v1.
    /// Os valores viajam por {DynamicResource} (e não por binding para a janela)
    /// para continuarem valendo quando a árvore é renderizada fora de uma Window.
    /// </summary>
    void Densidade(double w, double h)
    {
        // Como UserControl, Width/Height sao NaN antes da primeira medicao — e os
        // recursos responsivos alimentam MinHeight/Width via DynamicResource, entao
        // NaN quebraria o Measure. Parte de um tamanho plausivel ate o layout real.
        if (double.IsNaN(w) || w <= 0) w = 1280;
        if (double.IsNaN(h) || h <= 0) h = 800;
        if (w <= 0 || h <= 0 || Root == null) return;

        // 0 = apertado · 1 = normal · 2 = amplo · 3 = muito amplo
        var nivel = h < 740 ? 0 : h < 1000 ? 1 : h < 1300 ? 2 : 3;
        if (w < 1150 && nivel > 1) nivel = 1;

        double[] chip = [40, 50, 62, 74];
        double[] logo = [23, 29, 36, 43];
        double[] nome = [14, 16.5, 19.5, 22];
        double[] tag = [12, 12.5, 14, 15.5];
        double[] minimo = [58, 78, 108, 140];
        double[] titulo = [23, 28, 34, 40];
        Thickness[] pad =
        [
            new(12, 7, 12, 7), new(16, 12, 16, 12), new(22, 16, 22, 16), new(28, 20, 28, 20)
        ];

        var comSub = h >= 700;
        var chipTrilho = nivel == 0 ? 44 : 52;

        H1.FontSize = titulo[nivel];
        H1.LineHeight = Math.Ceiling(titulo[nivel] * 1.22);
        RailChip.Width = RailChip.Height = chipTrilho;
        RailLogo.Width = RailLogo.Height = nivel == 0 ? 25 : 30;
        TxtSelected.FontSize = nivel == 0 ? 16 : 18;
        H1Sub.Visibility = comSub ? Visibility.Visible : Visibility.Collapsed;
        HeaderNote.Visibility = w >= 1150 ? Visibility.Visible : Visibility.Collapsed;
        SpecRow.Visibility = w >= 1240 ? Visibility.Visible : Visibility.Collapsed;

        // Quantas linhas de descrição CABEM — em vez de um número fixo que a
        // faixa não comporta. Sem esta conta o cartão empurra a faixa, a faixa
        // empurra o tabuleiro e volta a aparecer rolagem (com cartão cortado
        // ao meio) exatamente nas resoluções mais comuns.
        var cabecalho = 44 + 24 + 13 + H1.LineHeight + (comSub ? 25 : 0);
        var trilho = 30 + chipTrilho;
        var porFaixa = (h - cabecalho - trilho - 10) / 4.0;
        var alturaCartao = porFaixa - 36 /* cabeçalho da faixa */ - 14 /* margem do item */;

        var entrelinha = Math.Round(tag[nivel] * 1.42);
        var alturaNome = Math.Ceiling(nome[nivel] * 1.34);
        var comMeta = h >= 840;
        var alturaMeta = comMeta ? Math.Round(tag[nivel] * 1.4) + 5 : 0;
        var sobra = alturaCartao - pad[nivel].Top * 2 - alturaNome - 4 - alturaMeta - 4;
        var linhas = Math.Clamp((int)Math.Floor(sobra / entrelinha), 0, 4);

        Def("UiChip", chip[nivel]);
        Def("UiLogo", logo[nivel]);
        Def("UiNome", nome[nivel]);
        Def("UiTag", tag[nivel]);
        // A entrelinha acompanha o corpo: sem isso o LineHeight fixo do estilo
        // engoliria a segunda linha da descrição nas telas menores.
        Def("UiTagLh", entrelinha);
        Def("UiTagMax", linhas > 0 ? linhas * entrelinha + 2 : 0);
        Def("UiCardMin", Math.Min(minimo[nivel], Math.Max(52, alturaCartao)));
        // A linha de mecanismo/arquivo é a primeira a sair quando falta altura:
        // ela também vive no trilho, com rótulo por extenso.
        Def("UiMetaMax", comMeta ? Math.Round(tag[nivel] * 1.4) : 0d);
        Root.Resources["UiCardPad"] = pad[nivel];
    }

    void Def(string chave, double valor) => Root.Resources[chave] = valor;

    // ---------------------------------------------------------------- animações

    /// <summary>
    /// Quando ligado, a entrada NÃO toca sozinha ao carregar — quem dá a partida é
    /// a abertura, no momento em que ela se dissolve. Sem isto a cascata de
    /// cartões acontecia atrás do splash e o usuário chegava numa tela já pronta.
    /// </summary>
    public bool DeferEntrance { get; set; }

    void View_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureAmbient();
        Densidade(ActualWidth, ActualHeight);

        if (DeferEntrance)
        {
            // Apagado desde já: a abertura acende no tempo dela, e assim não há um
            // quadro em que a tela aparece inteira antes de começar a entrar.
            Shell.Opacity = 0;
            return;
        }

        PlayEntrance();
    }

    /// <summary>
    /// A entrada do seletor: a tela acende, o cabeçalho sobe e os cartões entram
    /// em cascata.
    /// </summary>
    /// <param name="completa">
    /// Falso quando vem da abertura: ali o fundo JÁ está aceso (é o mesmo fundo,
    /// herdado do splash) e o cabeçalho já foi apresentado pela marca que pousou —
    /// repetir os dois seria uma segunda entrada por cima da primeira.
    /// </param>
    public void PlayEntrance(bool completa = true)
    {
        Shell.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)));

        if (completa)
        {
            Backdrop.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)));

            var sobe = new TranslateTransform();
            HeaderBand.RenderTransform = sobe;
            sobe.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-16, 0, TimeSpan.FromMilliseconds(520))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        // Os cartões só existem depois que os containers são gerados.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            AnimateCardsIn();
            Foco();
        }));
    }

    /// <summary>
    /// Para o fundo animado. O hospedeiro CHAMA isto ao fechar a janela e ao
    /// esconder a camada: sem parar e remover, cada exibição deixaria mais um
    /// punhado de relógios perpétuos rodando.
    /// </summary>
    /// <summary>
    /// Pausa/retoma o fundo animado. Sem isto ele rodava a plena carga com a janela
    /// EM SEGUNDO PLANO — medido em ~70% de um nucleo, e esta e a primeira tela de
    /// toda abertura. Pausar (e nao parar) preserva a fase: retomar nao da salto.
    /// </summary>
    public void PauseAmbient(bool pausar)
    {
        // PARAR o relogio, nao esconder nem "pausar":
        //  · Storyboard.Pause nao alcanca um storyboard iniciado com a sobrecarga de
        //    template (o caso do AmbientBackdrop) — a chamada nao acha o relogio;
        //  · esconder o Control corta a rasterizacao, mas o WPF continua avaliando as
        //    dezenas de animacoes do template.
        // Medido em segundo plano, com a tela de escolha aberta e a janela maximizada:
        // 57% de um nucleo pausando, 45% escondendo, 4% parando.
        //
        // Parar reinicia a fase na volta, entao o fundo entra com um fade curto — sem
        // ele o salto apareceria no instante em que o usuario volta para a janela.
        if (pausar) { StopAmbient(); return; }
        if (_ambient != null) return;

        EnsureAmbient();
        Backdrop.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));
    }

    public void StopAmbient()
    {
        if (_ambient == null) return;
        _ambient.Stop(Backdrop);
        _ambient.Remove(Backdrop);
        _ambient = null;
    }

    /// <summary>Cartões entram em cascata, na ordem do tabuleiro.</summary>
    void AnimateCardsIn()
    {
        var i = 0;
        foreach (var card in Descendentes<ListBoxItem>(LstOs))
        {
            var sobe = new TranslateTransform(0, 16);
            card.RenderTransform = sobe;
            card.Opacity = 0;

            var atraso = TimeSpan.FromMilliseconds(60 + i * 30);
            card.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = atraso });
            sobe.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(420))
                {
                    BeginTime = atraso,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            i++;
        }
    }

    static IEnumerable<T> Descendentes<T>(DependencyObject raiz) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(raiz); i++)
        {
            var filho = VisualTreeHelper.GetChild(raiz, i);
            if (filho is T achou) yield return achou;
            else
                foreach (var neto in Descendentes<T>(filho)) yield return neto;
        }
    }

    void EnsureAmbient()
    {
        if (_ambient != null) return;
        Backdrop.ApplyTemplate();
        _ambient = (Storyboard)FindResource("AmbientSb");
        // O fundo mora dentro de um ControlTemplate (para a MainWindow poder
        // reaproveitá-lo): o storyboard precisa do template para achar os nomes.
        _ambient.Begin(Backdrop, Backdrop.Template, isControllable: true);
    }

    /// <summary>
    /// Posiciona TODAS as animações do fundo num ponto determinístico do ciclo —
    /// <paramref name="fase"/> vai de 0 a 1 dentro de <see cref="AmbientCiclo"/>.
    /// Existe para o harness de render offscreen conseguir provar que o fundo se
    /// mexe: sem janela na tela não há relógio de composição, então usamos
    /// SeekAlignedToLastTick, que aplica os valores na hora.
    /// Como todos os períodos dividem o ciclo, fase 0 e fase 1 dão o mesmo quadro.
    /// </summary>
    /// <summary>Religa o fundo quando a camada volta a aparecer.</summary>
    public void StartAmbient() => EnsureAmbient();

    internal void PreviewSeek(double fase)
    {
        fase = Math.Clamp(fase, 0, 1);
        EnsureAmbient();
        _ambient!.SeekAlignedToLastTick(Backdrop, TimeSpan.FromSeconds(fase * AmbientCiclo),
                                        TimeSeekOrigin.BeginTime);
        _ambient!.Pause(Backdrop);
    }
}
