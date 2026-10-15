using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IsoForge;

namespace IsoForge.Ui;

/// <summary>
/// Os três pincéis VIVOS que o template <c>AmbientBackdrop</c> consome
/// (Theme.xaml): dois washes radiais e a cor das trilhas. Vivos = criados em C# e
/// nunca congelados; um pincel vindo de <c>ResourceDictionary</c> nasce frozen e
/// não aceita <c>ColorAnimation</c>.
///
/// Existe como classe própria porque agora DUAS telas tingem o mesmo fundo: o
/// tabuleiro de escolha (OsPickerView, que mantém a sua cópia por enquanto) e o
/// Shell. Repetir a montagem à mão na segunda garantiria que uma das duas
/// divergisse na primeira mudança.
/// </summary>
public sealed class BrandWash
{
    readonly SolidColorBrush _linhas = new(Colors.Transparent);
    readonly GradientStop[] _a = Paradas();
    readonly GradientStop[] _b = Paradas();

    public Brush WashA { get; }
    public Brush WashB { get; }
    public Brush Linhas => _linhas;

    public BrandWash()
    {
        WashA = Radial(_a);
        WashB = Radial(_b);
    }

    /// <summary>Aplica os três pincéis a um Control com o estilo AmbientSurface.</summary>
    public void LigarEm(System.Windows.Controls.Control fundo)
    {
        fundo.Background = WashA;
        fundo.BorderBrush = WashB;
        fundo.Foreground = _linhas;
    }

    /// <summary>
    /// Leva o fundo para a cor pedida. <paramref name="forca"/> escala os alfas:
    /// 1,0 é a intensidade do tabuleiro; no Shell usa-se ~0,45, porque ali o
    /// usuário lê formulários e o movimento tem de acompanhar sem competir.
    /// </summary>
    public void Tingir(Color cor, bool escuro, double forca, int ms)
    {
        var (a0, a1) = escuro ? (0xB4, 0x48) : (0x40, 0x1A);
        var (b0, b1) = escuro ? (0x78, 0x30) : (0x30, 0x14);

        Pintar(_a, cor, Alfa(a0, forca), Alfa(a1, forca), ms);
        Pintar(_b, cor, Alfa(b0, forca), Alfa(b1, forca), ms);

        if (ms <= 0) { _linhas.Color = cor; return; }
        _linhas.BeginAnimation(SolidColorBrush.ColorProperty, Fade(cor, 0xFF, ms));
    }

    static byte Alfa(int baseAlfa, double forca) => (byte)Math.Clamp(baseAlfa * forca, 0, 255);

    static void Pintar(GradientStop[] wash, Color cor, byte centro, byte meio, int ms)
    {
        byte[] alfas = [centro, meio, 0x00];
        for (var i = 0; i < wash.Length; i++)
        {
            if (ms <= 0) wash[i].Color = Marca.Alfa(cor, alfas[i]);
            else wash[i].BeginAnimation(GradientStop.ColorProperty, Fade(cor, alfas[i], ms));
        }
    }

    static ColorAnimation Fade(Color cor, byte a, int ms) =>
        new(Marca.Alfa(cor, a), TimeSpan.FromMilliseconds(ms))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

    static GradientStop[] Paradas() =>
    [
        new(Colors.Transparent, 0),
        new(Colors.Transparent, 0.45),
        new(Colors.Transparent, 1)
    ];

    static RadialGradientBrush Radial(GradientStop[] paradas) => new()
    {
        GradientOrigin = new Point(0.5, 0.5),
        Center = new Point(0.5, 0.5),
        RadiusX = 0.5,
        RadiusY = 0.5,
        GradientStops = new GradientStopCollection(paradas)
    };
}
