using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IsoForge;

/// <summary>
/// A abertura do IsoForge.
///
/// Duas coisas explicam o desenho deste arquivo:
///
///  1. <b>É uma camada, não uma janela.</b> A marca não some para a próxima tela
///     aparecer — ela VOA até a posição exata onde a marca fica na tela seguinte
///     (o cabeçalho da escolha de sistema, ou o logo da barra lateral). Como as
///     duas vivem na mesma árvore visual, dá para medir uma contra a outra com
///     <see cref="UIElement.TransformToVisual"/> e acertar o pouso no pixel.
///
///  2. <b>Nada além da marca sobrevive à saída.</b> Fundo, vinheta, anel e texto
///     se dissolvem; a marca continua opaca até pousar. É por isso que o fundo é
///     um retângulo com nome próprio em vez do <c>Background</c> do controle:
///     precisa desaparecer sozinho.
/// </summary>
public partial class SplashView : UserControl
{
    /// <summary>Disparado quando a abertura termina — por tempo ou porque pularam.</summary>
    public event EventHandler? Finished;

    Storyboard? _intro;
    bool _terminou;

    public SplashView()
    {
        InitializeComponent();

        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v != null) TxtVersao.Text = $"v{v.Major}.{v.Minor}.{v.Build}";

        // Quem já viu isto dez vezes não quer ver de novo.
        MouseLeftButtonDown += (_, __) => Pular();
        PreviewKeyDown += (_, __) => Pular();

        Loaded += (_, __) => Focus();
    }

    // ------------------------------------------------------------------
    // Entrada
    // ------------------------------------------------------------------

    /// <summary>
    /// Toca a abertura e, ao final, dispara <see cref="Finished"/>.
    /// </summary>
    /// <param name="jaMontada">
    /// Verdadeiro quando o splash NATIVO já mostrou a marca inteira enquanto a
    /// janela era construída. Aí a montagem não se repete — refazê-la seria a
    /// marca se desmanchar e montar de novo, que lê como defeito — e a camada só
    /// completa o giro do anel antes de sair.
    /// </param>
    public void Play(bool jaMontada = false)
    {
        EnsureAmbient();

        var sb = Montar();
        sb.Completed += (_, __) => Pular();
        _intro = sb;
        sb.Begin(this, isControllable: true);

        if (jaMontada)
            sb.SeekAlignedToLastTick(this, TimeSpan.FromSeconds(1.0), TimeSeekOrigin.BeginTime);

        // A parada é por relógio, não pelo fim do storyboard: assim o tempo de
        // leitura do texto não fica amarrado à duração da última animação.
        new DispatcherTimerLite(TimeSpan.FromMilliseconds(jaMontada ? 900 : 1750), Pular).Start();
    }

    /// <summary>
    /// Posiciona a abertura num instante determinístico. Existe para o harness de
    /// render offscreen: sem janela na tela não há relógio de composição, então
    /// <c>SeekAlignedToLastTick</c> é o que aplica os valores na hora.
    /// </summary>
    public void PreviewSeek(double segundos)
    {
        EnsureAmbient();
        var sb = Montar();
        _intro = sb;
        sb.Begin(this, isControllable: true);
        sb.SeekAlignedToLastTick(this, TimeSpan.FromSeconds(segundos), TimeSeekOrigin.BeginTime);
    }

    /// <summary>
    /// Deixa só a marca e a assinatura, sem fundo. É assim que se gera a PLACA
    /// estática do splash nativo — a imagem que o Windows mostra em ~200 ms,
    /// enquanto o WPF ainda está montando a janela (que leva segundos).
    /// </summary>
    public void PreviewPlate()
    {
        Congelar();   // solta o storyboard: senão ele segura o que apagamos aqui
        foreach (var e in new UIElement[] { Fundo, Backdrop, Vinheta, Rodape, Arco, Trilho })
            e.Opacity = 0;
    }

    /// <summary>Valores reais depois de um seek — diagnostico do harness.</summary>
    public (string, double)[] PreviewDump() =>
    [
        ("Marca.Op", Marca.Opacity),
        ("MarcaEscala", MarcaEscala.ScaleX),
        ("DiscoEscala", DiscoEscala.ScaleX),
        ("Trilha.Op", Trilha.Opacity),
        ("Cubo.Op", Cubo.Opacity),
        ("Faisca.Op", Faisca.Opacity),
        ("Arco.Op", Arco.Opacity),
        ("Wordmark.Op", Wordmark.Opacity),
    ];

    Storyboard Montar()
    {
        // Os alvos são por NOME, não por referência: Storyboard.SetTarget não
        // pega em Freezable — as opacidades funcionavam e NENHUMA transformação
        // aplicava (a marca ficava montada pela metade). SetTargetName resolve
        // pelo namescope do próprio controle e vale para os dois casos.
        var sb = new Storyboard();

        // A marca se monta de dentro para fora: pastilha, disco, trilha, cubo,
        // furo, faísca. Cada peça entra depois da anterior — é o atraso entre
        // elas que dá a leitura de "ligando", e não a duração de cada uma.
        Fade(sb, "Marca", 0, 1, 0.00, 0.34);
        Escala(sb, "MarcaEscala", 0.86, 1, 0.00, 0.46, Volta(0.35));
        Escala(sb, "DiscoEscala", 0.20, 1, 0.10, 0.52, Volta(0.30));
        Fade(sb, "Trilha", 0, 1, 0.26, 0.46);
        Escala(sb, "TrilhaEscala", 0.55, 1, 0.26, 0.56, Volta(0.40));
        Fade(sb, "Cubo", 0, 1, 0.40, 0.58);
        Fade(sb, "Furo", 0, 1, 0.46, 0.62);

        Fade(sb, "Faisca", 0, 1, 0.62, 0.76);
        Escala(sb, "FaiscaEscala", 0, 1, 0.62, 0.92, Volta(0.60));
        Angulo(sb, "FaiscaGiro", -60, 0, 0.62, 0.98);

        // O anel dá duas voltas e desacelera: sugere trabalho acontecendo sem
        // prometer um progresso que não temos como medir.
        Fade(sb, "Trilho", 0, 0.9, 0.18, 0.50);
        Fade(sb, "Arco", 0, 1, 0.18, 0.42);
        Angulo(sb, "ArcoGiro", -90, 630, 0.18, 1.60);

        Fade(sb, "Wordmark", 0, 1, 0.34, 0.62);
        Sobe(sb, "WordmarkSobe", 14, 0, 0.34, 0.70);
        Fade(sb, "Tagline", 0, 1, 0.48, 0.76);
        Sobe(sb, "TaglineSobe", 10, 0, 0.48, 0.82);
        Fade(sb, "Rodape", 0, 1, 0.80, 1.10);

        return sb;
    }

    void Pular()
    {
        if (_terminou) return;
        _terminou = true;
        Congelar();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Encerra a abertura no estado final e SOLTA as propriedades.
    ///
    /// O <c>Remove</c> não é zelo: um storyboard controlável continua segurando o
    /// que animou mesmo depois de <c>SkipToFill</c>, com precedência sobre valor
    /// local E sobre <c>BeginAnimation</c>. Sem soltar, o voo da saída não
    /// conseguia encolher a marca — ela atravessava a tela do tamanho de 132 px.
    /// Como soltar devolveria os valores do XAML (marca pela metade), o estado
    /// final é fixado à mão antes.
    /// </summary>
    void Congelar()
    {
        if (_intro == null) return;
        _intro.SkipToFill(this);
        _intro.Remove(this);
        _intro = null;

        // Remover o storyboard NÃO basta: as propriedades que ele animou continuam
        // presas ao relógio dele e ignoram tanto valor local quanto BeginAnimation
        // posterior. Sem limpar uma a uma, a saída não conseguia apagar o texto
        // nem o anel — e a placa estática saía com o rodapé em cima do nome.
        foreach (var e in new UIElement[]
                 { Marca, Trilha, Cubo, Furo, Faisca, Trilho, Arco, Wordmark, Tagline, Rodape })
            e.BeginAnimation(OpacityProperty, null);

        foreach (var t in new ScaleTransform[] { MarcaEscala, DiscoEscala, TrilhaEscala, FaiscaEscala })
        {
            t.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            t.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
        FaiscaGiro.BeginAnimation(RotateTransform.AngleProperty, null);
        ArcoGiro.BeginAnimation(RotateTransform.AngleProperty, null);
        WordmarkSobe.BeginAnimation(TranslateTransform.YProperty, null);
        TaglineSobe.BeginAnimation(TranslateTransform.YProperty, null);

        // Soltas, as propriedades voltariam ao XAML (marca pela metade): o estado
        // final da abertura é fixado à mão.
        Marca.Opacity = 1;
        MarcaEscala.ScaleX = MarcaEscala.ScaleY = 1;
        DiscoEscala.ScaleX = DiscoEscala.ScaleY = 1;
        Trilha.Opacity = 1;
        TrilhaEscala.ScaleX = TrilhaEscala.ScaleY = 1;
        Cubo.Opacity = 1;
        Furo.Opacity = 1;
        Faisca.Opacity = 1;
        FaiscaEscala.ScaleX = FaiscaEscala.ScaleY = 1;
        FaiscaGiro.Angle = 0;
        Trilho.Opacity = 0.9;
        Arco.Opacity = 1;
        Wordmark.Opacity = 1;
        WordmarkSobe.Y = 0;
        Tagline.Opacity = 1;
        TaglineSobe.Y = 0;
        Rodape.Opacity = 1;
    }

    // ------------------------------------------------------------------
    // Saída: a marca voa para o lugar dela na próxima tela
    // ------------------------------------------------------------------

    /// <summary>
    /// Dissolve tudo menos a marca e leva a marca até <paramref name="alvo"/>.
    /// Devolve quando o pouso termina — aí a camada pode ser recolhida, porque a
    /// marca de verdade já está exatamente embaixo.
    /// </summary>
    /// <param name="alvo">A marca da tela seguinte. Se for nula, só dissolve.</param>
    /// <param name="aoRevelar">
    /// Chamado quando o fundo começa a abrir (~120 ms), para a tela de baixo
    /// entrar JUNTO com a dissolução em vez de depois dela.
    /// </param>
    public async Task FlyToAsync(FrameworkElement? alvo, Action? aoRevelar = null)
    {
        Congelar();   // solta o que a abertura ainda segurava

        const int dissolve = 170;
        const int voo = 460;

        foreach (var e in new UIElement[] { Wordmark, Tagline, Rodape, Arco, Trilho })
            e.BeginAnimation(OpacityProperty, Rapido(e.Opacity, 0, dissolve));

        if (alvo != null && alvo.ActualWidth > 0 && Marca.ActualWidth > 0)
        {
            var origem = Caixa(Marca);
            var destino = Caixa(alvo);

            // A escala é em torno do centro (RenderTransformOrigin 0.5,0.5), então
            // o centro não se mexe sozinho: a translação é a diferença dos centros,
            // pura, sem correção de escala.
            var s = destino.Width / origem.Width;
            var dx = destino.Left + destino.Width / 2 - (origem.Left + origem.Width / 2);
            var dy = destino.Top + destino.Height / 2 - (origem.Top + origem.Height / 2);

            var suave = new CubicEase { EasingMode = EasingMode.EaseInOut };
            MarcaEscala.BeginAnimation(ScaleTransform.ScaleXProperty, Curva(1, s, voo, suave));
            MarcaEscala.BeginAnimation(ScaleTransform.ScaleYProperty, Curva(1, s, voo, suave));
            MarcaVoo.BeginAnimation(TranslateTransform.XProperty, Curva(0, dx, voo, suave));
            MarcaVoo.BeginAnimation(TranslateTransform.YProperty, Curva(0, dy, voo, suave));
        }

        await Task.Delay(120);
        aoRevelar?.Invoke();

        // O fundo abre DEPOIS que a tela de baixo já começou a entrar: sem isso
        // aparece um quadro de janela vazia entre as duas telas.
        foreach (var e in new UIElement[] { Fundo, Backdrop, Vinheta })
            e.BeginAnimation(OpacityProperty, Rapido(e.Opacity, 0, voo - 120));

        await Task.Delay(voo - 120);
    }

    /// <summary>
    /// Coloca a marca DIRETO no estado final do voo, sem animar, e apaga o
    /// cenário. Existe para o harness provar que o pouso cai em cima da marca de
    /// verdade: renderizado assim, as duas têm de coincidir no pixel.
    /// </summary>
    public string PreviewLanding(FrameworkElement alvo)
    {
        Congelar();
        foreach (var e in new UIElement[] { Wordmark, Tagline, Rodape, Arco, Trilho, Fundo, Backdrop, Vinheta })
            e.Opacity = 0;

        var origem = Caixa(Marca);
        var destino = Caixa(alvo);
        var s = destino.Width / origem.Width;
        var dx = destino.Left + destino.Width / 2 - (origem.Left + origem.Width / 2);
        var dy = destino.Top + destino.Height / 2 - (origem.Top + origem.Height / 2);

        MarcaEscala.ScaleX = MarcaEscala.ScaleY = s;
        MarcaVoo.X = dx;
        MarcaVoo.Y = dy;

        // A caixa pousada é CALCULADA, não medida: TransformToVisual devolve a
        // transformação do último passe de render, então logo após mexer no
        // RenderTransform ela ainda responde o estado antigo — e o diagnóstico
        // acusava 54 px de erro num pouso que estava certo.
        var pousada = new Rect(
            origem.Left + origem.Width / 2 + dx - origem.Width * s / 2,
            origem.Top + origem.Height / 2 + dy - origem.Height * s / 2,
            origem.Width * s, origem.Height * s);

        return $"origem {origem.Width:0}x{origem.Height:0} em ({origem.Left:0},{origem.Top:0})  ->  " +
               $"escala {s:0.000} desloca ({dx:0},{dy:0})" + Environment.NewLine +
               $"  pousa em ({pousada.Left:0.0},{pousada.Top:0.0}) {pousada.Width:0.0}x{pousada.Height:0.0}  " +
               $"alvo ({destino.Left:0.0},{destino.Top:0.0}) {destino.Width:0.0}x{destino.Height:0.0}  " +
               $"erro ({pousada.Left - destino.Left:+0.0;-0.0;0}, {pousada.Top - destino.Top:+0.0;-0.0;0}) px";
    }

    /// <summary>Caixa do elemento em coordenadas DESTA camada — o chão comum das duas telas.</summary>
    Rect Caixa(FrameworkElement e) =>
        e.TransformToVisual(this).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));

    // ------------------------------------------------------------------
    // Fundo animado
    // ------------------------------------------------------------------

    Storyboard? _ambient;

    /// <summary>
    /// Liga o mesmo fundo vivo da tela de escolha. É o que costura as duas telas:
    /// quando o splash se dissolve, o fundo por baixo é IGUAL — não há troca de
    /// cenário, só o conteúdo da frente mudando.
    /// </summary>
    void EnsureAmbient()
    {
        if (_ambient != null) return;
        Backdrop.ApplyTemplate();
        _ambient = (Storyboard)FindResource("AmbientSb");
        // O fundo mora dentro de um ControlTemplate: o storyboard precisa do
        // template para achar os nomes de dentro.
        _ambient.Begin(Backdrop, Backdrop.Template, isControllable: true);
    }

    /// <summary>
    /// Para o fundo animado. O hospedeiro chama isto ao recolher a camada: sem
    /// parar e remover, o relógio do fundo continua girando para sempre atrás de
    /// uma camada invisível — e este app já teve um bug de CPU exatamente assim.
    /// </summary>
    /// <summary>Pausa/retoma o fundo, pelo mesmo motivo do seletor.</summary>
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

    // ------------------------------------------------------------------
    // Açúcar: as animações da abertura em uma linha cada
    // ------------------------------------------------------------------

    static IEasingFunction Volta(double amplitude) =>
        new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = amplitude };

    static readonly IEasingFunction Saida = new CubicEase { EasingMode = EasingMode.EaseOut };

    static DoubleAnimation Curva(double de, double para, int ms, IEasingFunction? ease = null) =>
        new(de, para, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Saida };

    static DoubleAnimation Rapido(double de, double para, int ms) => Curva(de, para, ms);

    static void Aplicar(Storyboard sb, string alvo, string prop,
                        double de, double para, double t0, double t1, IEasingFunction? ease)
    {
        var a = new DoubleAnimation(de, para, TimeSpan.FromSeconds(t1 - t0))
        {
            BeginTime = TimeSpan.FromSeconds(t0),
            EasingFunction = ease ?? Saida,
            FillBehavior = FillBehavior.HoldEnd,
        };
        Storyboard.SetTargetName(a, alvo);
        Storyboard.SetTargetProperty(a, new PropertyPath(prop));
        sb.Children.Add(a);
    }

    static void Fade(Storyboard sb, string alvo, double de, double para, double t0, double t1)
        => Aplicar(sb, alvo, "Opacity", de, para, t0, t1, null);

    static void Escala(Storyboard sb, string alvo, double de, double para, double t0, double t1, IEasingFunction e)
    {
        Aplicar(sb, alvo, "ScaleX", de, para, t0, t1, e);
        Aplicar(sb, alvo, "ScaleY", de, para, t0, t1, e);
    }

    static void Angulo(Storyboard sb, string alvo, double de, double para, double t0, double t1)
        => Aplicar(sb, alvo, "Angle", de, para, t0, t1, null);

    static void Sobe(Storyboard sb, string alvo, double de, double para, double t0, double t1)
        => Aplicar(sb, alvo, "Y", de, para, t0, t1, null);
}

/// <summary>
/// Um disparo só, e some. Um <c>DispatcherTimer</c> nu fica vivo até alguém
/// lembrar de pará-lo; este se desliga sozinho no primeiro tique.
/// </summary>
sealed class DispatcherTimerLite
{
    readonly System.Windows.Threading.DispatcherTimer _t;

    public DispatcherTimerLite(TimeSpan quando, Action acao)
    {
        _t = new System.Windows.Threading.DispatcherTimer { Interval = quando };
        _t.Tick += (_, __) => { _t.Stop(); acao(); };
    }

    public void Start() => _t.Start();
}
