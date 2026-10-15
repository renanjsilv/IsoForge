using System.Windows;
using System.Windows.Controls;

namespace IsoForge.Ui;

/// <summary>
/// Painel das páginas do IsoForge: empilha os cartões em UMA coluna quando a
/// janela é estreita e em DUAS quando ela é larga.
///
/// Por que existe: as seis abas são pilhas verticais de cartões. Numa tela de
/// 2560 px isso vira ou um bloco estreito boiando no vazio (se limitarmos a
/// largura) ou um formulário com campos de 2 300 px (se não limitarmos). As duas
/// coisas são o mesmo defeito visto de ângulos diferentes: a página não tem uma
/// LARGURA DE LEITURA. Aqui a largura de leitura é fixa em <see cref="MinColumnWidth"/>
/// e o que sobra vira coluna — o preenchimento da tela sai por construção, sem
/// nenhum número mágico por resolução.
///
/// Regras:
///   · Nunca mais colunas do que cartões visíveis (uma coluna vazia é pior que
///     uma coluna larga).
///   · Cada cartão vai para a coluna MAIS BAIXA no momento, preservando a ordem
///     de declaração. Isso equilibra a altura sem embaralhar a leitura.
///   · Filhos Collapsed são ignorados por completo — <c>ApplyOsToUi()</c> esconde
///     cartões inteiros conforme o sistema escolhido, e uma coluna reservada para
///     um cartão invisível deixaria um buraco.
/// </summary>
public class CardFlow : Panel
{
    /// <summary>Largura de leitura: abaixo disso a página volta a ter uma coluna só.</summary>
    public static readonly DependencyProperty MinColumnWidthProperty =
        DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(CardFlow),
            new FrameworkPropertyMetadata(680d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    /// <summary>Calha entre as colunas (escala de 4 px — Space4).</summary>
    public static readonly DependencyProperty ColumnGapProperty =
        DependencyProperty.Register(nameof(ColumnGap), typeof(double), typeof(CardFlow),
            new FrameworkPropertyMetadata(20d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ColumnGap
    {
        get => (double)GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    /// <summary>Teto de colunas. Duas: três colunas de formulário não se leem.</summary>
    public static readonly DependencyProperty MaxColumnsProperty =
        DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(CardFlow),
            new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>Quantas colunas a última medição usou (diagnóstico do harness de render).</summary>
    public int ColumnCount { get; private set; } = 1;

    int Colunas(double largura, int visiveis)
    {
        if (visiveis <= 1) return 1;
        if (double.IsInfinity(largura) || largura <= 0) return 1;

        var gap = Math.Max(0, ColumnGap);
        var min = Math.Max(1, MinColumnWidth);
        // n colunas exigem n*min + (n-1)*gap de largura.
        var n = (int)Math.Floor((largura + gap) / (min + gap));
        return Math.Clamp(n, 1, Math.Min(Math.Max(1, MaxColumns), visiveis));
    }

    /// <summary>
    /// Distribui os filhos visíveis pelas colunas e devolve a altura de cada uma.
    /// A mesma conta serve para medir e para posicionar: com as mesmas larguras de
    /// coluna o resultado é idêntico nos dois passes.
    ///
    /// Com DUAS colunas a busca é EXAUSTIVA (2^n combinações, n pequeno): pôr cada
    /// cartão na coluna mais baixa do instante é guloso e erra — comprometia uma
    /// coluna antes de conhecer o cartão mais alto e a página passava a rolar por
    /// algumas dezenas de pixels que uma divisão equilibrada resolveria. Acima
    /// disso (ou com muitos cartões) cai no guloso, que é bom o bastante.
    /// </summary>
    double[] Distribuir(double larguraColuna, out int[] coluna)
    {
        coluna = new int[InternalChildren.Count];

        var indices = new List<int>();
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            if (InternalChildren[i].Visibility == Visibility.Collapsed) { coluna[i] = -1; continue; }
            indices.Add(i);
        }

        if (ColumnCount == 2 && indices.Count is > 0 and <= 14)
            return DuasColunasOtimas(indices, coluna);

        var alturas = new double[ColumnCount];
        foreach (var i in indices)
        {
            // coluna mais baixa; empate fica com a mais à esquerda (ordem de leitura)
            var alvo = 0;
            for (var c = 1; c < ColumnCount; c++)
                if (alturas[c] < alturas[alvo] - 0.5) alvo = c;

            coluna[i] = alvo;
            alturas[alvo] += InternalChildren[i].DesiredSize.Height;
        }
        return alturas;
    }

    /// <summary>
    /// Melhor divisão em duas colunas: minimiza a ALTURA DA MAIOR delas. Empates
    /// ficam com a distribuição que mantém os primeiros cartões à esquerda, para a
    /// leitura continuar de cima-esquerda para baixo-direita.
    /// </summary>
    double[] DuasColunasOtimas(List<int> indices, int[] coluna)
    {
        var n = indices.Count;
        var altura = new double[n];
        for (var k = 0; k < n; k++) altura[k] = InternalChildren[indices[k]].DesiredSize.Height;

        var melhorMax = double.MaxValue;
        var melhorEsq = -1.0;
        var melhorMascara = 0;

        // Bit k = 1 -> cartao k na coluna da direita. O cartao 0 fica sempre a
        // esquerda: as duas metades espelhadas dao a mesma altura, e fixar o
        // primeiro evita que a leitura comece pela direita.
        for (var mascara = 0; mascara < (1 << n); mascara += 2)
        {
            double esq = 0, dir = 0;
            for (var k = 0; k < n; k++)
                if ((mascara & (1 << k)) != 0) dir += altura[k]; else esq += altura[k];

            var maior = Math.Max(esq, dir);
            // Empate: prefere a esquerda mais cheia (cartoes iniciais a esquerda).
            if (maior < melhorMax - 0.5 || (Math.Abs(maior - melhorMax) <= 0.5 && esq > melhorEsq))
            {
                melhorMax = maior;
                melhorEsq = esq;
                melhorMascara = mascara;
            }
        }

        for (var k = 0; k < n; k++)
            coluna[indices[k]] = (melhorMascara & (1 << k)) != 0 ? 1 : 0;

        var alturas = new double[2];
        for (var k = 0; k < n; k++) alturas[coluna[indices[k]]] += altura[k];
        return alturas;
    }

    protected override Size MeasureOverride(Size disponivel)
    {
        var visiveis = 0;
        foreach (UIElement f in InternalChildren)
            if (f.Visibility != Visibility.Collapsed) visiveis++;

        ColumnCount = Colunas(disponivel.Width, visiveis);
        var gap = ColumnCount > 1 ? Math.Max(0, ColumnGap) : 0;
        var largura = double.IsInfinity(disponivel.Width)
            ? Math.Max(1, MinColumnWidth)
            : Math.Max(1, (disponivel.Width - gap * (ColumnCount - 1)) / ColumnCount);

        foreach (UIElement f in InternalChildren)
            f.Measure(f.Visibility == Visibility.Collapsed
                ? new Size(0, 0)
                : new Size(largura, double.PositiveInfinity));

        var alturas = Distribuir(largura, out _);
        var alta = 0d;
        foreach (var h in alturas) alta = Math.Max(alta, h);

        var total = double.IsInfinity(disponivel.Width)
            ? largura * ColumnCount + gap * (ColumnCount - 1)
            : disponivel.Width;
        return new Size(total, alta);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var gap = ColumnCount > 1 ? Math.Max(0, ColumnGap) : 0;
        var largura = Math.Max(1, (final.Width - gap * (ColumnCount - 1)) / ColumnCount);

        Distribuir(largura, out var coluna);

        var y = new double[ColumnCount];
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var filho = InternalChildren[i];
            if (coluna[i] < 0) { filho.Arrange(new Rect(0, 0, 0, 0)); continue; }

            var c = coluna[i];
            filho.Arrange(new Rect(c * (largura + gap), y[c], largura, filho.DesiredSize.Height));
            y[c] += filho.DesiredSize.Height;
        }
        return final;
    }
}
