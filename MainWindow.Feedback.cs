using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace IsoForge;

/// <summary>
/// Parte da janela principal responsável por MOSTRAR o trabalho longo.
///
/// O app baixa instaladores e compila ISOs por dezenas de minutos. O que existia
/// antes — uma barra e um texto que trocava de conteúdo — não distinguia "rodando"
/// de "travado", e o ponto do rodapé pulsava para sempre, inclusive parado.
/// Aqui cada estado usa um CANAL diferente (forma, movimento, cor), e todo loop
/// tem DesiredFrameRate explícito.
/// </summary>
public partial class MainWindow
{
    internal enum Atividade { Parado, Ocupado, Concluido, Falhou }

    Atividade _atividade = Atividade.Parado;

    // ------------------------------------------------------------------ texto
    /// <summary>
    /// Escreve a linha de status COM um pulso curto. Existe para toda mudança de
    /// etapa ser VISTA: um texto que só troca de conteúdo passa despercebido, e era
    /// por isso que uma geração de 20 minutos parecia travada.
    /// </summary>
    void Status(string texto)
    {
        if (TxtHeaderStatus == null) return;
        if (TxtHeaderStatus.Text == texto) return;   // nada mudou: não pisca à toa
        TxtHeaderStatus.Text = texto;

        if (TxtHeaderStatus.RenderTransform is not TranslateTransform t)
            TxtHeaderStatus.RenderTransform = t = new TranslateTransform();

        t.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        TxtHeaderStatus.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(160)));
    }

    // ------------------------------------------------------------------ estado
    /// <summary>Muda o indicador do rodapé e o texto ao mesmo tempo.</summary>
    void MostrarEstado(Atividade estado, string texto)
    {
        _atividade = estado;
        Status(texto);
        PararPulso();

        var comGlifo = estado is Atividade.Concluido or Atividade.Falhou;
        var falhou = estado == Atividade.Falhou;

        // Glifos por codigo, nao colados: E7BA e o alerta e E73E o visto
        // (Segoe MDL2 Assets). Caractere colado direto no .cs sobrevive mal a
        // ferramentas que reescrevem o arquivo em outra codificação.
        StatusGlyph.Text = falhou ? "" : "";
        StatusGlyph.SetResourceReference(TextBlock.ForegroundProperty, falhou ? "Danger" : "Ok");
        StatusDot.SetResourceReference(Shape.FillProperty,
            estado switch
            {
                Atividade.Ocupado => "OsAccent",
                Atividade.Falhou => "Danger",
                _ => "Ok"
            });

        StatusDot.Opacity = comGlifo ? 0 : 1;
        StatusGlyph.Opacity = comGlifo ? 1 : 0;
        StatusRing.Opacity = 0;

        if (comGlifo)
        {
            // Mesmo salto do selo do cartão de aplicativo (Theme.xaml, AppCard): o
            // vocabulário de "isto acabou de acontecer" é um só no app inteiro.
            var salta = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(300))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 } };
            StatusGlyphS.BeginAnimation(ScaleTransform.ScaleXProperty, salta);
            StatusGlyphS.BeginAnimation(ScaleTransform.ScaleYProperty, salta);
        }

        if (estado == Atividade.Ocupado) IniciarPulso();
    }

    void IniciarPulso()
    {
        Pulsar(StatusDot, OpacityProperty, 1, 0.3, 900, autoReverse: true);
        Pulsar(StatusRing, OpacityProperty, 0.55, 0, 1400, autoReverse: false);
        Pulsar(StatusRingS, ScaleTransform.ScaleXProperty, 0.4, 1.25, 1400, autoReverse: false);
        Pulsar(StatusRingS, ScaleTransform.ScaleYProperty, 0.4, 1.25, 1400, autoReverse: false);
    }

    void PararPulso()
    {
        StatusDot.BeginAnimation(OpacityProperty, null);
        StatusRing.BeginAnimation(OpacityProperty, null);
        StatusRingS.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        StatusRingS.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    /// <summary>
    /// Loop infinito com DesiredFrameRate EXPLÍCITO — a regra do app é que todo
    /// loop declare a sua taxa. Aqui são 30: o que muda é OPACIDADE e ESCALA de um
    /// anel de 16 px, e num ciclo de 900-1400 ms 30 é indistinguível de 60. A taxa
    /// cheia fica para o que percorre distância (fundos, brilho da barra).
    /// </summary>
    static void Pulsar(IAnimatable alvo, DependencyProperty prop,
                       double de, double para, int ms, bool autoReverse)
    {
        var a = new DoubleAnimation(de, para, TimeSpan.FromMilliseconds(ms))
        {
            AutoReverse = autoReverse,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(a, 30);
        alvo.BeginAnimation(prop, a);
    }

    // ------------------------------------------------------------------ progresso
    /// <summary>
    /// Mostra/esconde o painel de progresso da geração. Ele DESLIZA em vez de
    /// aparecer: uma barra brotando do nada lê como defeito de layout, e é
    /// justamente o momento em que o usuário mais olha para o rodapé.
    /// </summary>
    void MostrarProgresso(bool mostrar, int atrasoMs = 0)
    {
        if (BuildProgressPanel.RenderTransform is not TranslateTransform t)
            BuildProgressPanel.RenderTransform = t = new TranslateTransform();

        if (mostrar)
        {
            BuildProgressPanel.BeginAnimation(OpacityProperty, null);
            BuildProgressPanel.Opacity = 1;
            BuildProgressPanel.Visibility = Visibility.Visible;
            t.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(240))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            return;
        }

        // Esvaece ANTES de colapsar: colapsar direto reflui o rodapé num quadro só.
        var some = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180))
        { BeginTime = TimeSpan.FromMilliseconds(atrasoMs) };
        some.Completed += (_, __) => BuildProgressPanel.Visibility = Visibility.Collapsed;
        BuildProgressPanel.BeginAnimation(OpacityProperty, some);
    }

    /// <summary>
    /// Varredura de conclusão: um fio de 2 px cresce da esquerda para a direita no
    /// topo do rodapé e some, revelando de volta o fio de identidade do sistema.
    /// É o aviso de "terminou" que funciona mesmo com o usuário olhando para outra
    /// parte da tela — e, ao contrário de uma caixa de diálogo, não bloqueia nada.
    /// </summary>
    void VarrerConclusao(bool ok)
    {
        RailEdge.SetResourceReference(Border.BackgroundProperty, ok ? "Ok" : "Danger");

        // Solta a animação anterior antes de reescrever a base: sem isso o Opacity
        // fica preso no 0 em que a varredura passada terminou.
        RailEdge.BeginAnimation(OpacityProperty, null);
        RailEdge.Opacity = 1;

        RailEdgeS.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        RailEdge.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(700))
            { BeginTime = TimeSpan.FromMilliseconds(1100) });
    }
}
