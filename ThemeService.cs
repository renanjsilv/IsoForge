using System;
using System.Windows;
using System.Windows.Media;

namespace IsoForge;

/// <summary>
/// Tema escuro/claro da INTERFACE do IsoForge. O WPF congela (freeze) os brushes de um
/// ResourceDictionary do app, então não dá para mudar a cor deles em runtime. Em vez disso,
/// trocamos a ENTRADA do recurso por um brush novo; os controles referenciam essas cores por
/// {DynamicResource ...}, que re-resolve a entrada e atualiza a tela ao vivo.
/// </summary>
public static class ThemeService
{
    static void Set(string key, string hex)
    {
        var res = Application.Current?.Resources;
        if (res == null) return;
        res[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    /// <summary>Troca uma entrada por um pincel qualquer (gradientes, por exemplo).</summary>
    public static void SetBrush(string key, Brush brush)
    {
        var res = Application.Current?.Resources;
        if (res == null) return;
        brush.Freeze();
        res[key] = brush;
    }

    static Color Cor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static void SetDouble(string key, double valor)
    {
        var res = Application.Current?.Resources;
        if (res != null) res[key] = valor;
    }

    // ------------------------------------------------------------------
    // Barra de título (área NÃO cliente, desenhada pelo Windows)
    // ------------------------------------------------------------------

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int valor, int tamanho);

    // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE no Windows 11 e no 10 a partir da build
    // 18985; nas builds 1809 anteriores o mesmo atributo era o 19.
    const int DarkModeAtual = 20;
    const int DarkModeAntigo = 19;

    /// <summary>
    /// Pinta a barra de título de acordo com o tema.
    ///
    /// O WPF não tema a área não cliente: os botões de minimizar/maximizar/fechar
    /// e a faixa do título são desenhados pelo Windows, e continuavam BRANCOS com o
    /// app no escuro — foi o que o usuário reportou. Não há como resolver isso em
    /// XAML; é uma chamada ao DWM.
    ///
    /// Chamar depois de a janela ter handle (SourceInitialized) e de novo a cada
    /// troca de tema.
    /// </summary>
    public static void ApplyTitleBar(Window janela, bool dark)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(janela).Handle;
            if (hwnd == IntPtr.Zero) return;

            var valor = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DarkModeAtual, ref valor, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DarkModeAntigo, ref valor, sizeof(int));
        }
        catch
        {
            // Windows antigo sem o atributo: a barra fica clara, e só.
        }
    }

    /// <summary>Gradiente vertical de três paradas, para os fundos de superfície.</summary>
    static void Vertical(string key, string a, string b, string c) =>
        SetBrush(key, new LinearGradientBrush(
            new GradientStopCollection { new(Cor(a), 0), new(Cor(b), 0.6), new(Cor(c), 1) },
            new Point(0, 0), new Point(0, 1)));

    /// <summary>
    /// A BARRA LATERAL, por tema.
    ///
    /// Antes ela era escura nos DOIS temas — um bloco preto no tema claro, que é
    /// exatamente o que o usuário reportou. Agora ela é uma superfície como as
    /// outras: um pouco mais funda que a página, para continuar lendo como coluna
    /// separada, mas do mesmo mundo.
    ///
    /// Contrastes MEDIDOS contra a parada mais clara de cada gradiente:
    ///   claro:  texto #16233A 11,9:1 · secundário #4C5B75 5,4:1
    ///   escuro: texto #E6EDF6 15,4:1 · secundário #9FB0C6  8,2:1
    ///
    /// A VELA e o realce do fundo animado invertem com o tema: no claro, escurecer
    /// atrás de texto escuro pioraria o contraste — quem protege ali é o branco.
    /// </summary>
    static void BarraLateral(bool dark)
    {
        if (dark)
        {
            Vertical("SidebarGradient", "#0B1220", "#101A30", "#131E38");
            Set("SidebarBg", "#0B1220");
            Set("SidebarText", "#E6EDF6");
            Set("SidebarTextMuted", "#9FB0C6");
            Set("SidebarAccent", "#7FB0FF");
            Set("SidebarActiveBg", "#2E60A5FA");
            Set("SidebarHoverBg", "#16FFFFFF");
            Set("SidebarEdge", "#4DFFFFFF");
            Realce("#33FFFFFF", "#59FFFFFF");
            Vela("#B8000000", "#A0000000");
            Faixas(0.115, 0.090, 0.40);
            return;
        }

        Vertical("SidebarGradient", "#E7EDF9", "#DFE7F5", "#D7E1F2");
        Set("SidebarBg", "#E7EDF9");
        Set("SidebarText", "#16233A");
        // #3D4A61: escolhido para passar 4,5:1 contra o pixel MAIS ESCURO da barra
        // em qualquer das onze marcas, nao so contra o fundo tipico. O pior caso e
        // o carmim do Debian, onde o fundo local chega a #DBB7C9 — ali este token
        // mede 4,9:1 e o anterior (#4C5B75) media 4,2:1.
        Set("SidebarTextMuted", "#3D4A61");
        Set("SidebarAccent", "#1D4ED8");
        Set("SidebarActiveBg", "#382563EB");
        Set("SidebarHoverBg", "#14000000");
        Set("SidebarEdge", "#26000000");
        // No claro o realce continua BRANCO: sobre um fundo claro ele lê como luz,
        // não como mancha. O que inverte é a vela, não o realce.
        Realce("#66FFFFFF", "#8CFFFFFF");
        // Vela FRACA no claro, e de proposito. No escuro ela existe porque o fundo
        // animado CLAREIA atras de texto claro; no claro nada escurece — o realce e
        // branco e as brasas sao pequenas —, entao uma vela forte nao protegia nada
        // e criava uma faixa cinza visivel onde acabava.
        Vela("#4DFFFFFF", "#33FFFFFF");
        // Um TERCO da opacidade do escuro. A razao: uma cor saturada e escura sobre
        // fundo claro escurece muito mais do que a mesma cor clareia um fundo
        // escuro. Medido nas onze marcas, o carmim do Debian e o pior caso — a
        // 0,055 ele ainda deixava o texto secundario em 4,24:1.
        Faixas(0.038, 0.028, 0.16);
    }

    /// <summary>Opacidade das faixas do fundo da barra — ver o comentario em Theme.xaml.</summary>
    static void Faixas(double a, double b, double fio)
    {
        SetDouble("SidebarBandA", a);
        SetDouble("SidebarBandB", b);
        SetDouble("SidebarBandFio", fio);
    }

    /// <summary>Os dois realces radiais que derivam atrás da barra.</summary>
    static void Realce(string a, string b)
    {
        SetBrush("SidebarGlowA", new RadialGradientBrush(Cor(a), Cor("#00FFFFFF")));
        SetBrush("SidebarGlowB", new RadialGradientBrush(Cor(b), Cor("#00FFFFFF")));
    }

    /// <summary>
    /// As duas velas da barra: uma no topo (marca + menu) e uma no pé (versão e
    /// link). São gradientes porque precisam sumir sem borda visível.
    /// </summary>
    static void Vela(string forte, string medio)
    {
        SetBrush("SidebarScrimTop", new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Cor(forte), 0), new(Cor(medio), 0.62), new(Cor("#00000000"), 1)
            },
            new Point(0, 0), new Point(0, 1)));

        SetBrush("SidebarScrimBottom", new LinearGradientBrush(
            new GradientStopCollection { new(Cor(medio), 0), new(Cor("#00000000"), 1) },
            new Point(0, 1), new Point(0, 0)));
    }

    /// <summary>Fundo de página em três paradas (token AppSurfaceGradient).</summary>
    static void Superficie(string a, string b, string c) =>
        SetBrush("AppSurfaceGradient", new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Cor(a), 0), new(Cor(b), 0.55), new(Cor(c), 1)
            },
            new Point(0, 0), new Point(0.7, 1)));

    public static void Apply(bool dark)
    {
        if (dark)
        {
            Set("WindowBg", "#0D1117");
            Set("CardBg", "#161B22");
            Set("CardBorder", "#2A313C");
            Set("InputBorder", "#30363D");
            Set("TextMain", "#E6EDF3");
            Set("TextMuted", "#9FB0C4");
            Set("AccentSoft", "#17335C");
            // Caixas de aviso/info: fundo escuro, borda e texto claros e legíveis.
            Set("WarnBg", "#2A2410");
            Set("WarnBorder", "#5C4A16");
            Set("WarnText", "#FCD34D");
            Set("InfoBg", "#12233D");
            Set("InfoBorder", "#25436E");
            Set("InfoText", "#9EC5FE");
            Set("ChipBg", "#21262D");
            Set("InputDisabledBg", "#1B2027");
            Set("SubtleBg", "#12161C");
            // --- linguagem visual v2 (ver bloco "LINGUAGEM VISUAL v2" em Theme.xaml) ---
            Set("TextSecondary", "#C6D2E0");
            Set("SurfaceRaised", "#EE121926");
            Set("SurfaceHover", "#EE1E2836");
            Set("SurfaceScrim", "#C60B111C");
            Set("SurfaceGlass", "#D2141B27");
            Set("Elevation", "#40000000");
            Set("FocusRing", "#7AA7F5");
            Set("HoverRing", "#6B7C93");
            Set("OnAccent", "#FFFFFF");
            Set("AmbientLine", "#33FFFFFF");
            Set("AmbientDot", "#66FFFFFF");
            // Superficie rebaixada (console de atividade) e cinza de desabilitado.
            Set("SurfaceSunken", "#0A101B");
            Set("TextDisabled", "#79839A");
            Set("Ok", "#4ADE80");
            // Azul de TEXTO/icone: o Accent puro mede 2,7:1 sobre o cartao escuro.
            Set("AccentText", "#93B8FA");
            Superficie("#0B111C", "#101A2C", "#070B14");
            BarraLateral(dark: true);
        }
        else
        {
            Set("WindowBg", "#F1F4FB");
            Set("CardBg", "#FFFFFF");
            Set("CardBorder", "#E5EAF3");
            Set("InputBorder", "#CBD5E1");
            Set("TextMain", "#0F172A");
            Set("TextMuted", "#556076");
            Set("AccentSoft", "#DBEAFE");
            Set("WarnBg", "#FEF3C7");
            Set("WarnBorder", "#FCD34D");
            Set("WarnText", "#92400E");
            Set("InfoBg", "#EFF6FF");
            Set("InfoBorder", "#BFDBFE");
            Set("InfoText", "#1E3A8A");
            Set("ChipBg", "#F1F5F9");
            Set("InputDisabledBg", "#F1F5F9");
            Set("SubtleBg", "#F8FAFC");
            // --- linguagem visual v2 ---
            Set("TextSecondary", "#334155");
            Set("SurfaceRaised", "#F0FBFCFE");
            Set("SurfaceHover", "#F0E9F0FB");
            Set("SurfaceScrim", "#CCF1F5FC");
            Set("SurfaceGlass", "#D8FFFFFF");
            Set("Elevation", "#1A0B1220");
            Set("FocusRing", "#2563EB");
            Set("HoverRing", "#94A3BC");
            Set("OnAccent", "#FFFFFF");
            Set("AmbientLine", "#2E1B3A66");
            Set("AmbientDot", "#4D1B3A66");
            Set("SurfaceSunken", "#E6ECF8");
            Set("TextDisabled", "#7C879B");
            Set("Ok", "#15803D");
            Set("AccentText", "#1D4ED8");
            Superficie("#F7F9FE", "#E9EFFA", "#F4F7FD");
            BarraLateral(dark: false);
        }
    }
}
