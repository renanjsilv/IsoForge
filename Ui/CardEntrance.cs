using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // LayoutInformation
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace IsoForge.Ui;

/// <summary>
/// Entrada em cascata dos cartões de uma página.
///
/// Por que existe: a entrada antiga (o EventTrigger de Loaded no estilo TabPageFlow)
/// movia a PÁGINA INTEIRA como um bloco. Um bloco subindo lê como "a tela piscou";
/// uma cascata lê como "a página se montou" — e é o mesmo gesto do tabuleiro de
/// escolha do sistema (OsPickerView.AnimateCardsIn), que é justamente a tela de onde
/// o usuário acabou de vir.
///
/// A ordem NÃO pode ser a ordem de declaração: o CardFlow escolhe a coluna de cada
/// cartão resolvendo um problema de balanceamento (CardFlow.DuasColunasOtimas), então
/// o quinto cartão declarado pode ser o primeiro do olho. A ordem sai do RETÂNGULO
/// ARRANJADO, que é a única fonte de verdade sobre onde o cartão caiu.
///
/// Uso (no estilo da página, sem tocar em MainWindow.xaml):
///     &lt;Setter Property="ui:CardEntrance.Stagger" Value="True"/&gt;
/// e, quando a página muda de conteúdo sem recarregar (troca de sistema operacional):
///     Ui.CardEntrance.Play(cardFlow);
/// </summary>
public static class CardEntrance
{
    // ------------------------------------------------------------------ ajustes
    public static readonly DependencyProperty StaggerProperty =
        DependencyProperty.RegisterAttached("Stagger", typeof(bool), typeof(CardEntrance),
            new PropertyMetadata(false, StaggerMudou));

    public static void SetStagger(DependencyObject alvo, bool valor) => alvo.SetValue(StaggerProperty, valor);
    public static bool GetStagger(DependencyObject alvo) => (bool)alvo.GetValue(StaggerProperty);

    /// <summary>Milissegundos entre um cartão e o seguinte.</summary>
    public static readonly DependencyProperty StepProperty =
        DependencyProperty.RegisterAttached("Step", typeof(double), typeof(CardEntrance),
            new PropertyMetadata(34d));

    public static void SetStep(DependencyObject alvo, double valor) => alvo.SetValue(StepProperty, valor);
    public static double GetStep(DependencyObject alvo) => (double)alvo.GetValue(StepProperty);

    /// <summary>Altura do salto de entrada, em pixels.</summary>
    public static readonly DependencyProperty RiseProperty =
        DependencyProperty.RegisterAttached("Rise", typeof(double), typeof(CardEntrance),
            new PropertyMetadata(14d));

    public static void SetRise(DependencyObject alvo, double valor) => alvo.SetValue(RiseProperty, valor);
    public static double GetRise(DependencyObject alvo) => (double)alvo.GetValue(RiseProperty);

    /// <summary>
    /// Teto de degraus. Sem ele, uma aba de 8 cartões teria 7*34 = 238 ms só de
    /// espera antes do último começar — e a cascata deixaria de ser um gesto para
    /// virar uma fila. A partir do sexto cartão todos entram juntos.
    /// </summary>
    const int MaxDegraus = 5;

    static readonly Duration DurFade = new(TimeSpan.FromMilliseconds(200));
    static readonly Duration DurSobe = new(TimeSpan.FromMilliseconds(280));

    /// <summary>
    /// +1 = a aba nova está ABAIXO na barra lateral (o conteúdo entra de baixo);
    /// −1 = está acima (entra de cima).
    ///
    /// É estático de propósito: existe UMA thread de interface e UMA página
    /// entrando por vez, e quem escreve (MainTabs_SelectionChanged) roda no mesmo
    /// tique do dispatcher que dispara o Loaded da página nova. Guardar isto por
    /// painel exigiria alcançar o CardFlow da aba que ainda não carregou.
    /// </summary>
    public static int Direction { get; set; } = 1;

    // ------------------------------------------------------------------ ligação
    static void StaggerMudou(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel painel) return;
        painel.Loaded -= PainelCarregado;              // idempotente: o estilo pode reaplicar
        if ((bool)e.NewValue) painel.Loaded += PainelCarregado;
    }

    static void PainelCarregado(object sender, RoutedEventArgs e) => Play((Panel)sender);

    // ------------------------------------------------------------------ execução
    /// <summary>Repete a cascata. Ignorado se o painel ainda não está na árvore
    /// visual (o Loaded vai chamar de qualquer forma).</summary>
    public static void Play(Panel? painel)
    {
        if (painel is not { IsLoaded: true }) return;

        var cartoes = new List<FrameworkElement>();
        foreach (var filho in painel.Children)
            if (filho is FrameworkElement fe && fe.Visibility == Visibility.Visible)
                cartoes.Add(fe);
        if (cartoes.Count == 0) return;

        // Esconde AGORA, no mesmo quadro em que a página foi montada. Se esperássemos
        // o passe de layout, a página apareceria pronta por um quadro e só então
        // recomeçaria do zero — o "flash" clássico de animação de entrada.
        foreach (var c in cartoes) c.Opacity = 0;

        // ...e só anima depois do arrange, que é quem sabe em que coluna cada cartão
        // caiu. DispatcherPriority.Loaded roda depois do passe de layout: aqui os
        // retângulos já são os definitivos.
        painel.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => Animar(painel, cartoes)));
    }

    static void Animar(Panel painel, List<FrameworkElement> cartoes)
    {
        var passo = GetStep(painel);
        var salto = GetRise(painel) * (Direction < 0 ? -1 : 1);

        // Varredura DIAGONAL: quem está mais em cima entra antes e, no empate,
        // quem está à esquerda. O peso 0,25 na horizontal é o que costura as duas
        // colunas numa onda só — sem ele a página lê como duas listas paralelas
        // entrando ao mesmo tempo.
        var ordenados = cartoes
            .Select(c => (Card: c, Slot: LayoutInformation.GetLayoutSlot(c)))
            .OrderBy(p => p.Slot.Top + p.Slot.Left * 0.25)
            .Select(p => p.Card)
            .ToList();

        for (var i = 0; i < ordenados.Count; i++)
        {
            var card = ordenados[i];
            var atraso = TimeSpan.FromMilliseconds(Math.Min(i, MaxDegraus) * passo);

            // Reaproveita o transform: repetir a cascata (troca de sistema) não pode
            // empilhar transforms nem apagar um que o cartão já tenha por outro motivo.
            var desloca = card.RenderTransform as TranslateTransform;
            if (desloca == null &&
                (card.RenderTransform == null || card.RenderTransform == Transform.Identity))
                card.RenderTransform = desloca = new TranslateTransform();

            // Durante o BeginTime o relógio ainda está parado e a propriedade devolve
            // o valor BASE — que Play() acabou de pôr em 0. É por isso que o cartão
            // fica invisível durante a espera em vez de piscar.
            var surge = new DoubleAnimation(0, 1, DurFade) { BeginTime = atraso };
            surge.Completed += (_, __) =>
            {
                // Solta a propriedade. Enquanto a animação a segura, qualquer
                // card.Opacity = x feito por outro código é silenciosamente ignorado
                // — um defeito que não aparece em nenhum log.
                card.BeginAnimation(UIElement.OpacityProperty, null);
                card.Opacity = 1;
            };
            card.BeginAnimation(UIElement.OpacityProperty, surge);

            if (desloca == null) continue;   // cartão com transform próprio: só o fade

            var sobe = new DoubleAnimation(salto, 0, DurSobe)
            {
                BeginTime = atraso,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var alvo = desloca;
            sobe.Completed += (_, __) =>
            {
                alvo.BeginAnimation(TranslateTransform.YProperty, null);
                alvo.Y = 0;
            };
            desloca.BeginAnimation(TranslateTransform.YProperty, sobe);
        }
    }
}
