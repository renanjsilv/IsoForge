# Sistema de animação e elevação visual das 6 abas

> Especificação aplicável. Cada bloco de código diz **em que arquivo** e **em que ponto** entra.
> Aplique na ordem da seção 5 — cada passo é verificável sozinho.

---

## 1. Conceito

A tela de escolha do sistema já ensinou uma gramática de movimento: **cartões que sobem 16 px
e aparecem em cascata diagonal**, `CubicEase EaseOut`, hover que **eleva** em vez de piscar, e a
**cor da marca** entrando por um `ColorAnimation` num pincel vivo. As abas são a continuação
literal disso: os mesmos cartões, a mesma cascata (agora percorrendo as duas colunas do
`CardFlow`), o mesmo relevo no hover, e o mesmo fundo ambiente — só que agora tingido pelo
sistema que o usuário acabou de escolher, para a passagem de uma tela à outra não perder a cor.
O que muda de tela para tela ganha **direção** (a barra lateral é vertical: descer empurra a
página para cima); o que trabalha ganha **estado** (parado, ocupado, concluído, falhou), cada um
num canal diferente — forma, movimento e cor, nunca cor sozinha.

---

## 2. Inventário — o que JÁ anima (não refazer)

| O que | Onde | Valores |
|---|---|---|
| Fundo ambiente (arcos girando) | `Theme.xaml:308-514` — template `AmbientBackdrop` + storyboard `AmbientSb` | loop, `Timeline.DesiredFrameRate="20"`, períodos 120/60/40 s |
| Pausa do fundo ao perder foco | `MainWindow.Os.cs:30-62` — `StartBackdrop` / `PauseBackdrop` | — |
| Entrada da página (bloco inteiro) | `Theme.xaml:1368-1391` — `TabPageFlow`, `EventTrigger` de `Loaded` | Opacity 0→1 280 ms; TranslateY 16→0 300 ms CubicEase |
| Cartão de app (hover) | `Theme.xaml:576-595` | sombra 180 ms; scale 1.03 150 ms |
| Cartão de app (selo de marcado) | `Theme.xaml:603-624` | scale 0.4→1 320 ms `BackEase Amplitude=0.7` |
| Botão primário (hover/pressed) | `Theme.xaml:738-771` | `ColorAnimation` **hex cravado** 150 ms; scale 1.03 / 0.98 |
| Botão secundário | `Theme.xaml:797-816` | scale 1.02 120 ms |
| CTA (brilho) | `Theme.xaml:850-879` | glow Opacity 0→0.55 180 ms; scale 1.025 |
| CheckBox marcar/desmarcar | `Theme.xaml:1056-1084` | `ColorAnimation` **hex cravado**; check scale `BackEase` 180 ms |
| RadioButton marcar | `Theme.xaml:1146-1172` | idem, 200 ms |
| Item da barra lateral | `Theme.xaml:1306-1352` | selBg 200 ms; barra 240 ms; TranslateX 0→4 240 ms CubicEase |
| Seta do ComboBox | `Theme.xaml:1479-1496` | Rotate 0→180 180 ms |
| Chevron do Expander | `Theme.xaml:1668-1685` | Rotate 0→90 180 ms |
| ProgressBar indeterminada | `Theme.xaml:1569-1582` | pulso `Forever` **sem `DesiredFrameRate`** ← defeito |
| Ponto de status do rodapé | `MainWindow.xaml:922-932` | pulso `Forever` **sem `DesiredFrameRate` e sem estado** ← defeito |
| Camada de escolha (entra/sai) | `MainWindow.Os.cs:376-421` | Y ±22/26 280-300 ms; Shell scale 0.985→1 340 ms |
| Cascata do tabuleiro | `OsPickerView.xaml.cs:619-639` | atraso `60 + i*30` ms; Opacity 260 ms; Y 16→0 420 ms CubicEase |
| Cartão do tabuleiro (hover) | `OsPickerView.xaml:152-182` | halo 0→0.26; scale 1.014; lift Y −3; 160-220 ms |
| Cor da marca no trilho/fundo | `OsPickerView.xaml.cs:434-497` | `ColorAnimation` 380/620 ms em pincéis criados em C# |

**Dois defeitos de CPU já identificados no inventário** (corrigidos no passo 6): os dois únicos
loops infinitos fora do `AmbientSb` rodam a 60 fps, e o ponto de status pulsa mesmo com o app
em "Pronto" — um indicador de atividade que nunca para não indica nada.

---

## 3. Especificação

### 3.1 Coreografia de entrada da aba (cascata sobre o `CardFlow`)

**O que acontece.** Ao abrir uma aba, os cartões estão invisíveis no primeiro quadro e sobem em
cascata diagonal, do canto superior esquerdo para o inferior direito. A ordem é a ordem **visual**
(lida do retângulo que o `CardFlow` arranjou), não a ordem de declaração — porque o `CardFlow`
redistribui os cartões entre as colunas para equilibrar a altura (`Ui/CardFlow.cs:121-156`) e
cartões `Collapsed` são ignorados por completo.

**Números.** Por cartão: Opacity 0→1 em **200 ms**; TranslateY ±14→0 em **280 ms** `CubicEase EaseOut`.
Degrau da cascata: **34 ms**, com **teto de 5 degraus** — a página inteira assenta em ≤ 450 ms
mesmo com 8 cartões. O sinal do salto vem da direção da navegação (§3.2).

#### Arquivo NOVO: `Ui/CardEntrance.cs`

```csharp
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
        // caiu. DispatcherPriority.Loaded (6) roda DEPOIS de Render (7), que é onde o
        // WPF faz layout: aqui os retângulos já são os definitivos.
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
```

#### `Theme.xaml` — substituir o estilo `TabPageFlow` inteiro (linhas **1368-1391**)

```xml
    <!-- ================================================================
         PAGINA EM COLUNAS (ui:CardFlow)
         1560 = duas colunas de 770 com a calha de 20 — a largura em que um
         formulario ainda se le.

         A ENTRADA deixou de ser do PAINEL e passou a ser de cada CARTAO
         (ui:CardEntrance). O bloco inteiro subindo junto lia como "a tela
         piscou"; a cascata diagonal le como "a pagina se montou" — e e o
         mesmo gesto do tabuleiro de escolha do sistema.
         ================================================================ -->
    <Style x:Key="TabPageFlow" TargetType="ui:CardFlow">
        <Setter Property="MaxWidth" Value="1560"/>
        <Setter Property="MinColumnWidth" Value="620"/>
        <Setter Property="ColumnGap" Value="20"/>
        <Setter Property="ui:CardEntrance.Stagger" Value="True"/>
    </Style>
```

#### `MainWindow.Os.cs` — no FIM de `ApplyOsToUi()` (depois da linha **246**)

```csharp
        // A troca de sistema esconde/mostra cartões INTEIROS. Sem repetir a
        // cascata, a página se reconfigura entre dois quadros e o usuário não vê
        // o que mudou — vê só que "ficou diferente".
        if (MainTabs.SelectedItem is TabItem { Content: ScrollViewer { Content: Ui.CardFlow pagina } })
            Ui.CardEntrance.Play(pagina);
```

#### Opcional — cascata também nos chips de aplicativo

`MainWindow.xaml:451` — trocar `<WrapPanel>` por:

```xml
                                <WrapPanel ui:CardEntrance.Stagger="True" ui:CardEntrance.Step="18"
                                           ui:CardEntrance.Rise="10">
```

(18 ms porque são 10 chips pequenos; com 34 ms a fila fica longa demais para elementos desse tamanho.)

---

### 3.2 Transição entre abas, com direção

**O que acontece.** A barra lateral é vertical. Ir para uma aba **abaixo** faz o conteúdo novo
subir de baixo (`Rise = +14`); ir para uma **acima** faz descer de cima (`Rise = −14`). O título
da topbar — que é o único elemento que NÃO é recriado na troca (o binding só muda o texto) —
acompanha o mesmo eixo.

**Por que não há animação de saída.** O WPF desconecta o conteúdo da aba antiga no mesmo tique em
que conecta o novo; animar a saída exigiria adiar a troca (e aí quem clica rápido fica esperando)
ou fotografar a árvore com `VisualBrush` (caro, e borra texto). A direção da entrada já carrega o
eixo. **E é isto que torna o clique rápido seguro:** cada elemento recebe `BeginAnimation`, que
substitui a animação em voo naquele elemento; não há fila, nem `Completed` que mexa em estado
compartilhado, nem `await`.

**Números.** Título: TranslateY ±10→0 em **200 ms** `CubicEase EaseOut` + Opacity 0.25→1 em **180 ms**.

#### `MainWindow.xaml:86` — dar nome ao bloco do título da topbar

```xml
                <StackPanel x:Name="TopbarTitle" Orientation="Horizontal" VerticalAlignment="Center">
```

#### `MainWindow.xaml.cs` — em `MainTabs_SelectionChanged`, logo depois da linha **467**

Fica assim (as duas linhas originais de guarda permanecem):

```csharp
    void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl) return; // ignora SelectionChanged de listas/combos internos

        // DIREÇÃO DA NAVEGAÇÃO. A barra lateral é vertical: ir para uma aba mais
        // ABAIXO empurra a página para cima (o conteúdo novo sobe de baixo) e o
        // contrário para cima. Sem eixo, toda troca de aba é um corte seco.
        // Escrito ANTES do Loaded da página nova, que roda neste mesmo tique.
        var anterior = e.RemovedItems.Count > 0 ? MainTabs.Items.IndexOf(e.RemovedItems[0]) : -1;
        Ui.CardEntrance.Direction = anterior < 0 || MainTabs.SelectedIndex >= anterior ? 1 : -1;
        AnimarTituloDaAba(Ui.CardEntrance.Direction);

        if ((MainTabs.SelectedItem as TabItem)?.Header as string != "Drivers") return;
        // ... resto do método sem mudança ...
    }

    /// <summary>
    /// O título da topbar acompanha a direção da navegação. Ele é o único elemento
    /// da troca de aba que NÃO é recriado — o binding só troca o texto —, então sem
    /// este gesto é a única coisa da tela que muda de valor sem se mexer.
    /// </summary>
    void AnimarTituloDaAba(int direcao)
    {
        if (TopbarTitle == null) return;
        if (TopbarTitle.RenderTransform is not TranslateTransform t)
            TopbarTitle.RenderTransform = t = new TranslateTransform();

        t.BeginAnimation(TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(direcao * 10, 0, TimeSpan.FromMilliseconds(200))
            { EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
        TopbarTitle.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(180)));
    }
```

> `MainWindow.xaml.cs` ainda não importa `System.Windows.Media.Animation`. Ou adicione
> `using System.Windows.Media.Animation;` no topo e tire os prefixos, ou deixe como está acima.

---

### 3.3 Micro-interações nos controles

Todas em `Theme.xaml`. Cada uma diz o que substitui.

#### (a) Campo de texto — anel de foco + trilho que varre

Hoje o foco troca `BorderThickness` de 1 para 1.6 (`Theme.xaml:911`): isso **re-mede o campo** e o
texto anda meio pixel a cada foco. A espessura passa a ser constante e quem pinta o foco é um
segundo `Border` por cima. O trilho cresce por `ScaleX` (transform), nunca por `Width`.

**Substituir o `<Setter Property="Template">` do estilo `TextBox` (linhas 901-923):**

```xml
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                    <Grid>
                        <Border x:Name="b" Background="{TemplateBinding Background}" CornerRadius="8"
                                BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1"/>
                        <!-- Anel de foco POR CIMA da moldura. Engrossar a borda do
                             proprio 'b' re-media o campo: o texto andava meio pixel
                             a cada foco. Este Border so pinta, nao mede. -->
                        <Border x:Name="focus" CornerRadius="8" BorderThickness="1.6"
                                BorderBrush="{DynamicResource FocusRing}" Opacity="0"
                                IsHitTestVisible="False"/>
                        <!-- Trilho de foco: varre da esquerda para a direita por
                             ScaleX. Width animada re-mede o layout a cada quadro. -->
                        <Border x:Name="rail" Height="2" VerticalAlignment="Bottom" Margin="9,0,9,0"
                                CornerRadius="{StaticResource RadiusPill}" Opacity="0"
                                Background="{DynamicResource OsAccentText}"
                                RenderTransformOrigin="0,0.5" IsHitTestVisible="False">
                            <Border.RenderTransform><ScaleTransform x:Name="railS" ScaleX="0"/></Border.RenderTransform>
                        </Border>
                        <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsKeyboardFocused" Value="True">
                            <Trigger.EnterActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="focus" Storyboard.TargetProperty="Opacity" To="1" Duration="0:0:0.14"/>
                                        <DoubleAnimation Storyboard.TargetName="rail" Storyboard.TargetProperty="Opacity" To="1" Duration="0:0:0.1"/>
                                        <DoubleAnimation Storyboard.TargetName="railS" Storyboard.TargetProperty="ScaleX" To="1" Duration="0:0:0.18">
                                            <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
                                        </DoubleAnimation>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="focus" Storyboard.TargetProperty="Opacity" To="0" Duration="0:0:0.16"/>
                                        <DoubleAnimation Storyboard.TargetName="rail" Storyboard.TargetProperty="Opacity" To="0" Duration="0:0:0.14"/>
                                        <DoubleAnimation Storyboard.TargetName="railS" Storyboard.TargetProperty="ScaleX" To="0" Duration="0:0:0.14"/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.ExitActions>
                        </Trigger>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource HoverRing}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="b" Property="Background" Value="{DynamicResource InputDisabledBg}"/>
                            <Setter Property="Foreground" Value="{DynamicResource TextDisabled}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
```

**O mesmo template vale para `PasswordBox` (linhas 934-956)** — trocar `TargetType="TextBox"` por
`TargetType="PasswordBox"` e manter tudo igual.

#### (b) ComboBox — foco animado + abertura com corpo

**No template (linha 1464-1465), acrescentar o anel de foco depois do `chrome`:**

```xml
                        <Border x:Name="chrome" Background="{DynamicResource CardBg}" CornerRadius="8"
                                BorderBrush="{DynamicResource InputBorder}" BorderThickness="1"/>
                        <Border x:Name="focus" CornerRadius="8" BorderThickness="1.6"
                                BorderBrush="{DynamicResource FocusRing}" Opacity="0" IsHitTestVisible="False"/>
```

**No `Popup` (linhas 1515-1526), dar transform ao Border e origem no topo:**

```xml
                        <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom"
                               AllowsTransparency="True" PopupAnimation="Fade" StaysOpen="False">
                            <Border Background="{DynamicResource CardBg}" CornerRadius="10"
                                    BorderBrush="{DynamicResource CardBorder}" BorderThickness="1"
                                    MinWidth="{TemplateBinding ActualWidth}" MaxHeight="280"
                                    Margin="0,4,12,12" Padding="0,4" RenderTransformOrigin="0.5,0">
                                <Border.RenderTransform>
                                    <TransformGroup>
                                        <ScaleTransform x:Name="popS" ScaleY="1"/>
                                        <TranslateTransform x:Name="popT"/>
                                    </TransformGroup>
                                </Border.RenderTransform>
                                <Border.Effect>
                                    <DropShadowEffect Color="#0F172A" BlurRadius="16" ShadowDepth="4" Direction="270" Opacity="0.18"/>
                                </Border.Effect>
                                <ScrollViewer VerticalScrollBarVisibility="Auto">
                                    <ItemsPresenter/>
                                </ScrollViewer>
                            </Border>
                        </Popup>
```

> `PopupAnimation` passou de `Slide` para `Fade`: o Slide do sistema é um deslocamento
> de janela, com temporização e curva que não são as nossas. O corpo do gesto vem do
> `ScaleY`/`Y` abaixo; o Fade só cobre o fechamento, onde não dá para animar nada
> (a popup já sumiu).

**Substituir os três `Trigger`s de borda (linhas 1537-1545) por:**

```xml
                        <MultiTrigger>
                            <MultiTrigger.Conditions>
                                <Condition Property="IsMouseOver" Value="True"/>
                                <Condition Property="IsKeyboardFocusWithin" Value="False"/>
                            </MultiTrigger.Conditions>
                            <Setter TargetName="chrome" Property="BorderBrush" Value="{DynamicResource HoverRing}"/>
                        </MultiTrigger>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True">
                            <Trigger.EnterActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="focus" Storyboard.TargetProperty="Opacity" To="1" Duration="0:0:0.14"/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="focus" Storyboard.TargetProperty="Opacity" To="0" Duration="0:0:0.16"/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.ExitActions>
                        </Trigger>
                        <Trigger Property="IsDropDownOpen" Value="True">
                            <Setter TargetName="chrome" Property="BorderBrush" Value="{DynamicResource OsAccent}"/>
                            <Trigger.EnterActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="popS" Storyboard.TargetProperty="ScaleY"
                                                         From="0.9" To="1" Duration="0:0:0.15">
                                            <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
                                        </DoubleAnimation>
                                        <DoubleAnimation Storyboard.TargetName="popT" Storyboard.TargetProperty="Y"
                                                         From="-6" To="0" Duration="0:0:0.15">
                                            <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
                                        </DoubleAnimation>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                        </Trigger>
```

#### (c) CheckBox e RadioButton — matar o hex cravado

O `ColorAnimation` com `#2563EB` e `#94A3B8` (`Theme.xaml:1061-1062, 1077, 1151, 1165`) é o motivo
de a marcação não seguir nem o tema nem o sistema escolhido. Troca-se a animação de COR por uma
animação de OPACIDADE sobre um preenchimento que já vem do token.

**CheckBox — substituir o `<Border x:Name="box" ...>` (linhas 1027-1042) por:**

```xml
                        <Border x:Name="box" Width="19" Height="19" CornerRadius="4" BorderThickness="1.6"
                                BorderBrush="{DynamicResource HoverRing}" Background="Transparent"
                                VerticalAlignment="Center">
                            <Grid>
                                <!-- Preenchimento marcado. Margin negativa = ele cobre
                                     TAMBEM a borda de 1,6 px do proprio 'box', entao o
                                     contorno cinza some sem precisar de um segundo
                                     Setter. Antes isto era um ColorAnimation com dois
                                     hex cravados no XAML: nem tema, nem sistema. -->
                                <Border x:Name="fill" CornerRadius="4" Margin="-1.6" Opacity="0"
                                        Background="{DynamicResource OsAccent}"/>
                                <Path x:Name="check" Data="M 4,10 L 8,14 L 15,5"
                                      Stroke="{DynamicResource OnAccent}" StrokeThickness="2.4"
                                      StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                                      RenderTransformOrigin="0.5,0.5" Opacity="0">
                                    <Path.RenderTransform>
                                        <ScaleTransform x:Name="checkScale" ScaleX="0.4" ScaleY="0.4"/>
                                    </Path.RenderTransform>
                                </Path>
                            </Grid>
                        </Border>
```

**E as duas animações de cor viram uma de opacidade** (dentro do `Trigger IsChecked`, linhas 1061-1062 e 1076-1077):

```xml
                                        <!-- entrada -->
                                        <DoubleAnimation Storyboard.TargetName="fill" Storyboard.TargetProperty="Opacity" To="1" Duration="0:0:0.15"/>
```
```xml
                                        <!-- saída -->
                                        <DoubleAnimation Storyboard.TargetName="fill" Storyboard.TargetProperty="Opacity" To="0" Duration="0:0:0.15"/>
```

**A sombra do hover (linha 1088)** passa a usar um token de cor:

```xml
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="box" Property="Effect">
                                <Setter.Value>
                                    <DropShadowEffect Color="{DynamicResource OsAccentColor}" BlurRadius="8" ShadowDepth="0" Opacity="0.35"/>
                                </Setter.Value>
                            </Setter>
                        </Trigger>
```

**RadioButton — substituir o `<Border x:Name="ring" ...>` (linhas 1121-1132) por:**

```xml
                        <Border x:Name="ring" Width="19" Height="19" CornerRadius="10" BorderThickness="1.6"
                                BorderBrush="{DynamicResource HoverRing}"
                                Background="{DynamicResource CardBg}" VerticalAlignment="Center">
                            <Grid>
                                <Border x:Name="ringOn" CornerRadius="10" Margin="-1.6" BorderThickness="1.6"
                                        BorderBrush="{DynamicResource OsAccent}" Opacity="0"/>
                                <Ellipse x:Name="dot" Width="9" Height="9" Fill="{DynamicResource OsAccent}"
                                         RenderTransformOrigin="0.5,0.5" Opacity="0">
                                    <Ellipse.RenderTransform>
                                        <ScaleTransform x:Name="dotScale" ScaleX="0.3" ScaleY="0.3"/>
                                    </Ellipse.RenderTransform>
                                </Ellipse>
                            </Grid>
                        </Border>
```

e trocar os dois `ColorAnimation` de `bd` (1151 e 1165) por
`<DoubleAnimation Storyboard.TargetName="ringOn" Storyboard.TargetProperty="Opacity" To="1" Duration="0:0:0.15"/>`
e `To="0"`.

#### (d) Botão — véu de estado no lugar de três hex

**Substituir o template do estilo implícito `Button` (linhas 726-777):**

```xml
                <ControlTemplate TargetType="Button">
                    <Border x:Name="b" CornerRadius="9" Background="{DynamicResource OsAccent}"
                            SnapsToDevicePixels="True" RenderTransformOrigin="0.5,0.5">
                        <Border.RenderTransform>
                            <ScaleTransform x:Name="sc" ScaleX="1" ScaleY="1"/>
                        </Border.RenderTransform>
                        <Grid>
                            <!-- VEU DE ESTADO. O hover era um ColorAnimation com
                                 "#1D4ED8" e o pressed com "#1E40AF": tres hex que nao
                                 seguem nem o tema nem o sistema escolhido. Um veu
                                 preto so ESCURECE, entao o branco do rotulo nunca
                                 perde contraste, seja qual for a marca por baixo.
                                 (#000000 aqui nao e cor de tema: e sombra.) -->
                            <Border x:Name="veil" CornerRadius="9" Background="#000000" Opacity="0"/>
                            <ContentPresenter Margin="{TemplateBinding Padding}"
                                              HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Grid>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Trigger.EnterActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="veil" Storyboard.TargetProperty="Opacity" To="0.14" Duration="0:0:0.15"/>
                                        <DoubleAnimation Storyboard.TargetName="sc" Storyboard.TargetProperty="ScaleX" To="1.03" Duration="0:0:0.12"/>
                                        <DoubleAnimation Storyboard.TargetName="sc" Storyboard.TargetProperty="ScaleY" To="1.03" Duration="0:0:0.12"/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="veil" Storyboard.TargetProperty="Opacity" To="0" Duration="0:0:0.2"/>
                                        <DoubleAnimation Storyboard.TargetName="sc" Storyboard.TargetProperty="ScaleX" To="1" Duration="0:0:0.18"/>
                                        <DoubleAnimation Storyboard.TargetName="sc" Storyboard.TargetProperty="ScaleY" To="1" Duration="0:0:0.18"/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.ExitActions>
                        </Trigger>
                        <Trigger Property="IsPressed" Value="True">
                            <Trigger.EnterActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="veil" Storyboard.TargetProperty="Opacity" To="0.26" Duration="0:0:0.05"/>
                                        <DoubleAnimation Storyboard.TargetName="sc" Storyboard.TargetProperty="ScaleX" To="0.98" Duration="0:0:0.05"/>
                                        <DoubleAnimation Storyboard.TargetName="sc" Storyboard.TargetProperty="ScaleY" To="0.98" Duration="0:0:0.05"/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="b" Property="Background" Value="{DynamicResource InputDisabledBg}"/>
                            <Setter Property="Foreground" Value="{DynamicResource TextDisabled}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
```

**Botão secundário (linhas 797-799)** — trocar o `Setter` instantâneo por um véu animado. Dentro
do template (linha 788), acrescentar o véu:

```xml
                    <Border x:Name="b" CornerRadius="9" BorderThickness="1"
                            Background="{DynamicResource CardBg}" BorderBrush="{DynamicResource InputBorder}"
                            RenderTransformOrigin="0.5,0.5">
                        <Border.RenderTransform>
                            <ScaleTransform x:Name="sc" ScaleX="1" ScaleY="1"/>
                        </Border.RenderTransform>
                        <Grid>
                            <Border x:Name="tint" CornerRadius="9" Background="{DynamicResource OsAccentSoft}" Opacity="0"/>
                            <ContentPresenter Margin="{TemplateBinding Padding}"
                                              HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Grid>
                    </Border>
```

e no `Trigger IsMouseOver`: trocar `<Setter TargetName="b" Property="Background" .../>` por
`<Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource OsAccent}"/>` mais, no
storyboard de entrada/saída, `<DoubleAnimation Storyboard.TargetName="tint" Storyboard.TargetProperty="Opacity" To="1"/0" Duration="0:0:0.14"/>`.
Remover o `Padding` do `Border` (foi para o `ContentPresenter`).

#### (e) Expander — o corpo abre em vez de aparecer

**Substituir a linha 1690 e o trigger 1692-1696:**

```xml
                        <!-- O corpo desliza e esvaece. A ALTURA nao e animada de
                             proposito (Height animada re-mede a pagina a cada
                             quadro); o refluxo acontece uma vez so, no comeco, e o
                             conteudo entra por cima dele. -->
                        <Border x:Name="body" Visibility="Collapsed" Opacity="0">
                            <Border.RenderTransform><TranslateTransform x:Name="bodyT" Y="-8"/></Border.RenderTransform>
                            <ContentPresenter/>
                        </Border>
                    </StackPanel>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsExpanded" Value="True">
                            <Trigger.EnterActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <ObjectAnimationUsingKeyFrames Storyboard.TargetName="body" Storyboard.TargetProperty="Visibility">
                                            <DiscreteObjectKeyFrame KeyTime="0:0:0" Value="{x:Static Visibility.Visible}"/>
                                        </ObjectAnimationUsingKeyFrames>
                                        <DoubleAnimation Storyboard.TargetName="body" Storyboard.TargetProperty="Opacity" To="1" Duration="0:0:0.16"/>
                                        <DoubleAnimation Storyboard.TargetName="bodyT" Storyboard.TargetProperty="Y" To="0" Duration="0:0:0.2">
                                            <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
                                        </DoubleAnimation>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="body" Storyboard.TargetProperty="Opacity" To="0" Duration="0:0:0.12"/>
                                        <DoubleAnimation Storyboard.TargetName="bodyT" Storyboard.TargetProperty="Y" To="-8" Duration="0:0:0.12"/>
                                        <!-- Colapsa DEPOIS do fade: colapsar junto faz o
                                             refluxo e o desaparecimento no mesmo quadro. -->
                                        <ObjectAnimationUsingKeyFrames Storyboard.TargetName="body" Storyboard.TargetProperty="Visibility">
                                            <DiscreteObjectKeyFrame KeyTime="0:0:0.12" Value="{x:Static Visibility.Collapsed}"/>
                                        </ObjectAnimationUsingKeyFrames>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.ExitActions>
                        </Trigger>
                    </ControlTemplate.Triggers>
```

**E o cabeçalho ganha hover** — no `ToggleTheme` interno (linha 1657), acrescentar ao
`ControlTemplate.Triggers` do `ToggleButton`:

```xml
                                        <Trigger Property="IsMouseOver" Value="True">
                                            <Setter Property="Background" Value="{DynamicResource SurfaceHover}"/>
                                        </Trigger>
```
(o `Border` do template precisa então usar `Background="{TemplateBinding Background}"` e o
`ToggleButton` receber `Background="{DynamicResource SubtleBg}"` como padrão.)

#### (f) Item de lista (drivers, modelos) — marca de seleção que cresce

**Substituir o template de `ListBoxItem` (linhas 987-1003):**

```xml
                <ControlTemplate TargetType="ListBoxItem">
                    <Grid>
                        <Border x:Name="b" Background="Transparent" CornerRadius="6" Padding="{TemplateBinding Padding}">
                            <ContentPresenter>
                                <ContentPresenter.RenderTransform><TranslateTransform x:Name="tx"/></ContentPresenter.RenderTransform>
                            </ContentPresenter>
                        </Border>
                        <!-- Marca de selecionado: uma barra que CRESCE a esquerda.
                             Cor sozinha nao comunica estado — e numa lista de 400
                             modelos a linha selecionada tem de saltar sem procurar. -->
                        <Border x:Name="mark" Width="3" HorizontalAlignment="Left" Margin="0,4"
                                CornerRadius="{StaticResource RadiusPill}" Opacity="0"
                                Background="{DynamicResource OsAccent}" RenderTransformOrigin="0.5,0.5">
                            <Border.RenderTransform><ScaleTransform x:Name="markS" ScaleY="0.2"/></Border.RenderTransform>
                        </Border>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="b" Property="Background" Value="{DynamicResource SurfaceHover}"/>
                            <Trigger.EnterActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="tx" Storyboard.TargetProperty="X" To="3" Duration="0:0:0.13">
                                            <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
                                        </DoubleAnimation>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="tx" Storyboard.TargetProperty="X" To="0" Duration="0:0:0.16"/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.ExitActions>
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="b" Property="Background" Value="{DynamicResource OsAccentSoft}"/>
                            <Setter Property="FontWeight" Value="SemiBold"/>
                            <Trigger.EnterActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="mark" Storyboard.TargetProperty="Opacity" To="1" Duration="0:0:0.12"/>
                                        <DoubleAnimation Storyboard.TargetName="markS" Storyboard.TargetProperty="ScaleY" To="1" Duration="0:0:0.22">
                                            <DoubleAnimation.EasingFunction><BackEase EasingMode="EaseOut" Amplitude="0.4"/></DoubleAnimation.EasingFunction>
                                        </DoubleAnimation>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="mark" Storyboard.TargetProperty="Opacity" To="0" Duration="0:0:0.12"/>
                                        <DoubleAnimation Storyboard.TargetName="markS" Storyboard.TargetProperty="ScaleY" To="0.2" Duration="0:0:0.12"/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.ExitActions>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
```

> `FontWeight="SemiBold"` no selecionado é o segundo canal: quem não distingue a cor
> ainda vê o peso mudar. `AccentSoft` continua sendo o fundo, mas agora pelo token do
> sistema.

#### (g) DataGrid — linha com marca lateral (passo mais invasivo, deixe por último)

Exige `xmlns:prim="clr-namespace:System.Windows.Controls.Primitives;assembly=PresentationFramework"`
na raiz de `Theme.xaml`.

**Substituir o estilo `DataGridRow` (linhas 1613-1622):**

```xml
    <Style TargetType="DataGridRow">
        <Setter Property="Template">
            <Setter.Value>
                <!-- Template SIMPLIFICADO de proposito: sem SelectiveScrollingGrid,
                     sem DataGridRowHeader e sem DataGridDetailsPresenter. As duas
                     grades do IsoForge (GridVpn e GridUnits) usam
                     HeadersVisibility="Column", nao tem detalhes de linha nem
                     colunas congeladas — a maquinaria toda estava sendo instanciada
                     para nao fazer nada. Se algum desses tres recursos passar a ser
                     usado, este template TEM de voltar ao completo. -->
                <ControlTemplate TargetType="DataGridRow">
                    <Border x:Name="row" Background="{TemplateBinding Background}" SnapsToDevicePixels="True">
                        <Grid>
                            <Border x:Name="tint" Background="{DynamicResource OsAccentSoft}" Opacity="0"/>
                            <Border x:Name="mark" Width="3" HorizontalAlignment="Left" Opacity="0"
                                    Background="{DynamicResource OsAccent}" RenderTransformOrigin="0.5,0.5">
                                <Border.RenderTransform><ScaleTransform x:Name="markS" ScaleY="0.2"/></Border.RenderTransform>
                            </Border>
                            <prim:DataGridCellsPresenter ItemsPanel="{TemplateBinding ItemsPanel}"
                                                         SnapsToDevicePixels="{TemplateBinding SnapsToDevicePixels}"/>
                        </Grid>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="row" Property="Background" Value="{DynamicResource SurfaceHover}"/>
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Trigger.EnterActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="tint" Storyboard.TargetProperty="Opacity" To="1" Duration="0:0:0.14"/>
                                        <DoubleAnimation Storyboard.TargetName="mark" Storyboard.TargetProperty="Opacity" To="1" Duration="0:0:0.12"/>
                                        <DoubleAnimation Storyboard.TargetName="markS" Storyboard.TargetProperty="ScaleY" To="1" Duration="0:0:0.2">
                                            <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
                                        </DoubleAnimation>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="tint" Storyboard.TargetProperty="Opacity" To="0" Duration="0:0:0.16"/>
                                        <DoubleAnimation Storyboard.TargetName="mark" Storyboard.TargetProperty="Opacity" To="0" Duration="0:0:0.12"/>
                                        <DoubleAnimation Storyboard.TargetName="markS" Storyboard.TargetProperty="ScaleY" To="0.2" Duration="0:0:0.12"/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.ExitActions>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

---

### 3.4 Feedback de estado — ocupado, concluído, falhou

Hoje o app tem uma `ProgressBar` e um texto. O ponto pulsante do rodapé
(`MainWindow.xaml:922-932`) pulsa **para sempre**, inclusive com o status em "Pronto": é o
oposto de um indicador de atividade. E o pulso da `ProgressBar` indeterminada
(`Theme.xaml:1573-1577`) roda a 60 fps.

**O desenho.** Quatro estados, cada um em um canal diferente:

| Estado | Forma | Movimento | Cor |
|---|---|---|---|
| Parado | ponto sólido 9 px | nenhum | `Ok` |
| Ocupado | ponto + anel | ponto pulsa 900 ms · anel abre 1400 ms (**15 fps**) | `OsAccent` |
| Concluído | glifo de visto | salta `BackEase` 300 ms + varredura no rodapé | `Ok` |
| Falhou | glifo de alerta | nenhum | `Danger` |

E dois acréscimos: a **linha de status pulsa a cada mudança de texto** (senão uma geração de
20 minutos parece travada, mesmo trocando de etapa), e a barra ganha um **brilho que corre**,
ativo somente enquanto ela está visível.

#### `MainWindow.xaml` — substituir o chip de status (linhas 920-940)

```xml
                    <Border DockPanel.Dock="Right" Background="{DynamicResource ChipBg}" CornerRadius="10"
                            Padding="14,8" VerticalAlignment="Center">
                        <StackPanel Orientation="Horizontal">
                            <!-- INDICADOR DE ESTADO. Tres canais, nao so cor:
                                 parado = ponto imovel · ocupado = ponto pulsando com
                                 anel que abre · concluido/falhou = glifo que salta.
                                 O pulso perpetuo que existia aqui rodava tambem em
                                 "Pronto" — um indicador que nunca para nao indica. -->
                            <Grid Width="16" Height="16" VerticalAlignment="Center" Margin="0,0,9,0">
                                <Ellipse x:Name="StatusRing" Width="16" Height="16" Opacity="0"
                                         Stroke="{DynamicResource OsAccent}" StrokeThickness="1.5"
                                         RenderTransformOrigin="0.5,0.5">
                                    <Ellipse.RenderTransform>
                                        <ScaleTransform x:Name="StatusRingS" ScaleX="0.4" ScaleY="0.4"/>
                                    </Ellipse.RenderTransform>
                                </Ellipse>
                                <Ellipse x:Name="StatusDot" AutomationProperties.Name="Indicador de atividade"
                                         Width="9" Height="9" Fill="{DynamicResource Ok}"
                                         HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                <TextBlock x:Name="StatusGlyph" Text="&#xE73E;" FontFamily="Segoe MDL2 Assets"
                                           FontSize="11" Foreground="{DynamicResource Ok}" Opacity="0"
                                           HorizontalAlignment="Center" VerticalAlignment="Center"
                                           RenderTransformOrigin="0.5,0.5">
                                    <TextBlock.RenderTransform>
                                        <ScaleTransform x:Name="StatusGlyphS" ScaleX="0.4" ScaleY="0.4"/>
                                    </TextBlock.RenderTransform>
                                </TextBlock>
                            </Grid>
                            <TextBlock x:Name="TxtHeaderStatus" Foreground="{DynamicResource TextMuted}" FontSize="12.5"
                                       FontWeight="SemiBold" VerticalAlignment="Center" MaxWidth="360"
                                       TextTrimming="CharacterEllipsis" TextWrapping="NoWrap" Text="Pronto"
                                       ToolTip="{Binding Text, RelativeSource={RelativeSource Self}}"/>
                            <ProgressBar x:Name="DownloadBar" Width="130" Height="8" Margin="12,0,0,0"
                                         Minimum="0" Maximum="100" Visibility="Collapsed" VerticalAlignment="Center"/>
                        </StackPanel>
                    </Border>
```

#### `MainWindow.xaml` — envolver o rodapé para ganhar o fio de identidade e a varredura

Substituir a abertura do rodapé (linha 909) e o fechamento (linha 961):

```xml
        <!-- ============ RODAPÉ: ações + status/downloads ============ -->
        <Grid Grid.Column="1" Grid.Row="2">
            <Border Background="{DynamicResource CardBg}" BorderBrush="{DynamicResource CardBorder}"
                    BorderThickness="0,1,0,0" Padding="24,14">
                <StackPanel>
                    <!-- ... conteúdo do rodapé, sem mudança (DockPanel + BuildProgressPanel) ... -->
                </StackPanel>
            </Border>

            <!-- Fio de identidade: os mesmos 2 px na cor do sistema que o trilho da
                 tela de escolha usa. E o eco visual mais barato entre as duas telas. -->
            <Border Height="2" VerticalAlignment="Top" IsHitTestVisible="False"
                    Background="{DynamicResource OsAccentGradient}"/>
            <!-- Varredura de conclusao: passa por cima do fio, em verde ou vermelho,
                 e some revelando o fio de volta. -->
            <Border x:Name="RailEdge" Height="2" VerticalAlignment="Top" Opacity="0"
                    IsHitTestVisible="False" RenderTransformOrigin="0,0.5"
                    Background="{DynamicResource Ok}">
                <Border.RenderTransform><ScaleTransform x:Name="RailEdgeS" ScaleX="0"/></Border.RenderTransform>
            </Border>
        </Grid>
```

#### Arquivo NOVO: `MainWindow.Feedback.cs`

```csharp
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

        // Glifos por codigo, nao colados: \uE7BA e o alerta e \uE73E o visto
        // (Segoe MDL2 Assets). Caractere colado direto no .cs sobrevive mal a
        // ferramentas que reescrevem o arquivo em outra codificacao.
        StatusGlyph.Text = falhou ? "\uE7BA" : "\uE73E";
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
            // Mesmo salto do selo do cartão de aplicativo (Theme.xaml AppCard):
            // o vocabulário de "isto acabou de acontecer" é um só no app inteiro.
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
    /// Loop infinito com DesiredFrameRate EXPLÍCITO. A regra não é do fundo
    /// animado, é do app: um loop a 60 fps custa CPU parado, mesmo num ponto de
    /// 9 px — e o fundo já provou isso com 56% de um núcleo. Num fade de 900 ms
    /// 15 fps é indistinguível de 60.
    /// </summary>
    static void Pulsar(IAnimatable alvo, DependencyProperty prop,
                       double de, double para, int ms, bool autoReverse)
    {
        var a = new DoubleAnimation(de, para, TimeSpan.FromMilliseconds(ms))
        {
            AutoReverse = autoReverse,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(a, 15);
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
```

#### `MainWindow.xaml.cs` — ligar o estado

**`SetBusy` (linhas 1481-1489):**

```csharp
    void SetBusy(bool busy)
    {
        BtnBuild.IsEnabled = !busy;
        BtnDryRun.IsEnabled = !busy;
        BtnTestScript.IsEnabled = !busy;
        if (BtnSandbox != null) BtnSandbox.IsEnabled = !busy;
        if (!busy) HideDownloadBar();
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;

        // O indicador só volta a "parado" se não estamos exibindo um resultado:
        // um "ISO gerada" que dura meio segundo não informa ninguém.
        if (busy) MostrarEstado(Atividade.Ocupado, TxtHeaderStatus.Text);
        else if (_atividade == Atividade.Ocupado) MostrarEstado(Atividade.Parado, "Pronto");
    }
```

**`Build_Click` (linhas 1353-1382)** — três linhas trocadas:

```csharp
        BuildProgress.Value = 0;
        MostrarProgresso(true);                                   // era: Visibility = Visible
        try
        {
            AppendLog($"==== Iniciando geração da ISO ({OsCatalog.NameOf(cfg.Os)}) ====");
            // ... sem mudança ...
            MostrarEstado(Atividade.Concluido, "ISO gerada");      // NOVO, no fim do try
            VarrerConclusao(true);                                 // NOVO
        }
        catch (Exception ex)
        {
            AppendLog($"ERRO: {ex.Message}");
            MostrarEstado(Atividade.Falhou, "A geração falhou");   // NOVO
            VarrerConclusao(false);                                // NOVO
            MessageBox.Show(this, ex.Message, "IsoForge — erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            MostrarProgresso(false, atrasoMs: 1200);   // era: Visibility = Collapsed
        }
```

**As demais escritas em `TxtHeaderStatus.Text` viram `Status(...)`** — troca mecânica, nas
linhas **157, 185, 188, 203, 213, 372, 507, 511, 934, 939** de `MainWindow.xaml.cs` e
`MainWindow.Os.cs`. (A leitura em 372 continua lendo `TxtHeaderStatus.Text`.)

#### `Theme.xaml` — ProgressBar com brilho que corre

**Substituir o template inteiro do estilo `ProgressBar` (linhas 1558-1585):**

```xml
                <ControlTemplate TargetType="ProgressBar">
                    <Grid x:Name="PART_Track" SnapsToDevicePixels="True">
                        <Border Background="{TemplateBinding Background}" CornerRadius="6"/>
                        <Border x:Name="PART_Indicator" Background="{DynamicResource OsAccentGradient}"
                                CornerRadius="6" HorizontalAlignment="Left" ClipToBounds="True">
                            <!-- BRILHO QUE CORRE. A barra parada por 40 s de compilacao
                                 lia como travada mesmo com a porcentagem subindo. As
                                 coordenadas do gradiente sao RELATIVAS a caixa, entao
                                 a mesma animacao serve para a barra de 130 px do chip
                                 e para a de ~900 px da geracao — sem calcular largura.
                                 x:Name no pincel e obrigatorio: e o que o faz nascer
                                 por instancia (mutavel) em vez de congelado. -->
                            <Rectangle x:Name="shine" RadiusX="6" RadiusY="6">
                                <Rectangle.Fill>
                                    <LinearGradientBrush x:Name="shineBrush" StartPoint="-0.6,0" EndPoint="-0.2,0">
                                        <GradientStop Color="#00FFFFFF" Offset="0"/>
                                        <GradientStop Color="#4DFFFFFF" Offset="0.5"/>
                                        <GradientStop Color="#00FFFFFF" Offset="1"/>
                                    </LinearGradientBrush>
                                </Rectangle.Fill>
                            </Rectangle>
                        </Border>
                        <!-- pulso do modo indeterminado -->
                        <Border x:Name="pulse" Background="{DynamicResource OsAccentGradient}" CornerRadius="6" Opacity="0"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <!-- O brilho so existe enquanto a barra esta na tela. IsVisible
                             cobre tudo: barra colapsada, aba trocada e janela escondida. -->
                        <Trigger Property="IsVisible" Value="True">
                            <Trigger.EnterActions>
                                <BeginStoryboard x:Name="shineSb">
                                    <Storyboard RepeatBehavior="Forever" Timeline.DesiredFrameRate="20">
                                        <PointAnimation Storyboard.TargetName="shineBrush" Storyboard.TargetProperty="StartPoint"
                                                        From="-0.6,0" To="1.0,0" Duration="0:0:1.6"/>
                                        <PointAnimation Storyboard.TargetName="shineBrush" Storyboard.TargetProperty="EndPoint"
                                                        From="-0.2,0" To="1.4,0" Duration="0:0:1.6"/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <StopStoryboard BeginStoryboardName="shineSb"/>
                            </Trigger.ExitActions>
                        </Trigger>
                        <Trigger Property="IsIndeterminate" Value="True">
                            <Setter TargetName="PART_Indicator" Property="Visibility" Value="Collapsed"/>
                            <Trigger.EnterActions>
                                <BeginStoryboard x:Name="pulseSb">
                                    <!-- DesiredFrameRate: este loop rodava a 60 fps. -->
                                    <Storyboard RepeatBehavior="Forever" AutoReverse="True" Timeline.DesiredFrameRate="15">
                                        <DoubleAnimation Storyboard.TargetName="pulse" Storyboard.TargetProperty="Opacity"
                                                         From="0.2" To="0.75" Duration="0:0:0.7"/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <StopStoryboard BeginStoryboardName="pulseSb"/>
                            </Trigger.ExitActions>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
```

---

### 3.5 Elevação visual dos cartões

**O que muda no `GroupBox`:** ganha relevo no hover (um `Border` sólido deslocado, **não** um
`DropShadowEffect`: são 6 a 8 cartões por página e oito blurs de 16 px custam mais do que o app
inteiro), um véu de identidade do sistema no canto superior esquerdo, e um **slot de ícone
opcional** no cabeçalho, alimentado pelo `Tag` — que hoje está livre em todos os `GroupBox` de
`MainWindow.xaml`. Sem `Tag`, o cartão fica exatamente como está.

**Sem lift e sem scale no hover, de propósito:** o cartão do tabuleiro é um alvo de clique; estes
são recipientes. Passar o mouse pela página não pode fazer os cartões pularem enquanto se digita
dentro deles.

#### `Theme.xaml` — substituir o estilo `GroupBox` inteiro (linhas 1196-1219)

```xml
    <!-- ============================================================
         GROUPBOX = CARTAO
         ============================================================
         Relevo: um Border solido deslocado (token Elevation), NAO um
         DropShadowEffect — sao 6 a 8 cartoes por pagina e oito blurs de 16 px
         custam mais do que o app inteiro. O relevo e ESTADO DE HOVER, nao
         decoracao permanente (linguagem v2).

         Identidade: um veu radial do acento do SISTEMA no canto superior
         esquerdo (OsAccentWash). Alfa baixo e area restrita ao cabecalho: a aba
         do Ubuntu respira laranja sem tingir texto nenhum.

         Icone: opcional, vem do Tag do GroupBox (glifo Segoe MDL2). Sem Tag a
         coluna some — os cartoes que nao receberem icone nao mudam em nada.
         ============================================================ -->
    <Style TargetType="GroupBox">
        <Setter Property="Foreground" Value="{DynamicResource TextMain}"/>
        <Setter Property="Margin" Value="0,0,0,16"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="GroupBox">
                    <Grid>
                        <Border x:Name="halo" CornerRadius="{StaticResource RadiusLg}"
                                Background="{DynamicResource Elevation}"
                                Margin="5,7,5,-5" Opacity="0" IsHitTestVisible="False"/>

                        <Border x:Name="card" CornerRadius="{StaticResource RadiusLg}"
                                Background="{DynamicResource CardBg}"
                                BorderBrush="{DynamicResource CardBorder}" BorderThickness="1">
                            <Grid>
                                <Border CornerRadius="{StaticResource RadiusLg}" IsHitTestVisible="False"
                                        Background="{DynamicResource OsAccentWash}"/>

                                <DockPanel Margin="18,16,18,16">
                                    <Grid DockPanel.Dock="Top" Margin="0,0,0,13">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="Auto"/>
                                            <ColumnDefinition Width="Auto"/>
                                            <ColumnDefinition Width="*"/>
                                        </Grid.ColumnDefinitions>

                                        <Border Width="3" Height="17" CornerRadius="{StaticResource RadiusPill}"
                                                Background="{DynamicResource OsAccentGradient}"
                                                VerticalAlignment="Center" Margin="0,0,10,0"/>

                                        <Border x:Name="glyphWrap" Grid.Column="1" Width="26" Height="26"
                                                CornerRadius="{StaticResource RadiusSm}" Margin="0,0,10,0"
                                                Background="{DynamicResource ChipBg}" VerticalAlignment="Center">
                                            <TextBlock x:Name="glyph" Text="{TemplateBinding Tag}"
                                                       FontFamily="Segoe MDL2 Assets" FontSize="13"
                                                       Foreground="{DynamicResource TextMuted}"
                                                       HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                        </Border>

                                        <TextBlock Grid.Column="2" Text="{TemplateBinding Header}"
                                                   Style="{StaticResource TitleSection}"/>
                                    </Grid>
                                    <ContentPresenter/>
                                </DockPanel>
                            </Grid>
                        </Border>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <!-- Sem Tag: nada de quadradinho vazio no cabecalho. -->
                        <Trigger Property="Tag" Value="{x:Null}">
                            <Setter TargetName="glyphWrap" Property="Visibility" Value="Collapsed"/>
                        </Trigger>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="card" Property="BorderBrush" Value="{DynamicResource HoverRing}"/>
                            <Setter TargetName="glyphWrap" Property="Background" Value="{DynamicResource OsAccentSoft}"/>
                            <Setter TargetName="glyph" Property="Foreground" Value="{DynamicResource OsAccentText}"/>
                            <Trigger.EnterActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="halo" Storyboard.TargetProperty="Opacity"
                                                         To="1" Duration="0:0:0.16"/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.EnterActions>
                            <Trigger.ExitActions>
                                <BeginStoryboard>
                                    <Storyboard>
                                        <DoubleAnimation Storyboard.TargetName="halo" Storyboard.TargetProperty="Opacity"
                                                         To="0" Duration="0:0:0.22"/>
                                    </Storyboard>
                                </BeginStoryboard>
                            </Trigger.ExitActions>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

> Se o relevo ficar pesado no tema escuro (`Elevation` = `#40000000`, 25% de preto),
> baixe o `To="1"` do halo para `0.6`. É o único número a calibrar aqui.

#### `MainWindow.xaml` — ícones dos cartões (opcional, um atributo por cartão)

Acrescentar `Tag` ao `GroupBox`. Sugestão de glifos Segoe MDL2, por aba:

| Cartão (linha) | `Tag` | Glifo |
|---|---|---|
| Imagem ISO (121) | `&#xE958;` | disco |
| Como entregar (166) | `&#xE7B8;` | pacote |
| Instalação do disco (178) | `&#xEDA2;` | disco rígido |
| Primeiro boot / Rede (188) | `&#xE701;` | wifi |
| Modo de implantação (221) | `&#xE912;` | nuvem |
| Usuário local (238) | `&#xE77B;` | pessoa |
| Sistema / Sistema Linux (272 / 313) | `&#xE770;` | engrenagem |
| Disco de destino (380) | `&#xEDA2;` | disco rígido |
| Opções da instalação (419) | `&#xE9D9;` | lista |
| Escolha os programas (442) | `&#xE896;` | download |
| Opções do Office (489) | `&#xE8A5;` | documento |
| Opções do FortiClient (535) | `&#xE72E;` | cadeado |
| Drivers no Linux (598) / do fabricante (614) | `&#xE950;` | chip |
| Componentes a instalar (686) | `&#xE71D;` | grade |
| Aparência (716) | `&#xE790;` | paleta |
| Otimização (767 / 780) | `&#xEC4A;` | acelerar |
| Relatório (791) | `&#xE9F9;` | relatório |
| Nome automático (799) | `&#xE8AC;` | renomear |
| Imagem golden (864) | `&#xE735;` | estrela |

---

### 3.6 Identidade por sistema operacional

A `OsInfo.Accent` (`Models/TargetOs.cs:82-135`) só era usada no tabuleiro. Passa a alimentar o
Shell inteiro por meio de seis entradas de recurso que um serviço troca em runtime — exatamente o
mecanismo que o `ThemeService` já usa (`ThemeService.cs:14-28`: brushes de dicionário são
congelados, então não se muda a cor, muda-se a ENTRADA; quem consome por `{DynamicResource}`
re-resolve na hora).

**Onde a marca aparece:** o fio de 3 px no cabeçalho de cada cartão · o véu do canto do cartão ·
o botão primário e o CTA · a caixa marcada, o rádio e o trilho de foco dos campos · a marca de
seleção das listas e das grades · o item ativo da barra lateral · o fio de 2 px no topo do rodapé
· e o **fundo ambiente** (§3.6.2), que é o que costura visualmente a tela de escolha com o Shell.

**O que NÃO segue a marca, de propósito:** o `FocusRing`. Foco é uma affordance de acessibilidade
e precisa ser previsível — se ele também mudasse com o sistema, num Fedora azul o foco, a seleção
e o acento seriam a mesma cor e os três estados se confundiriam.

**Legibilidade garantida por construção, não por sorte.** As três derivações usam os utilitários
já provados de `Marca` (`OsPickerView.xaml.cs:20-90`), cada uma com um contrato:

| Derivado | Contrato | Como |
|---|---|---|
| `OsAccent` (superfície preenchida) | branco em cima ≥ 4,5:1 | `Marca.Tinta` — escurece até o branco alcançar 4,5:1 |
| `OsAccentText` (texto/ícone sobre superfície) | ≥ 5,5:1 contra o `CardBg` do tema | escurece (claro) / clareia (escuro) até medir |
| `OsAccentSoft` (véu) | é véu: nunca fica atrás de texto pequeno sem que o texto seja `OsAccentText` | alfa 0x24 claro / 0x38 escuro |

A margem de 5,5:1 (e não 4,5) existe porque o `OsAccentText` também é usado **por cima do próprio
véu suave** (chip de app, cabeçalho em hover), e o véu tira ~0,4 do contraste do fundo.

#### Arquivo NOVO: `OsAccentService.cs`

```csharp
using System.Windows;
using System.Windows.Media;
using IsoForge.Models;

namespace IsoForge;

/// <summary>
/// Leva a cor de marca do sistema escolhido para o Shell inteiro.
///
/// Por que existe: OsInfo.Accent alimentava só o tabuleiro de escolha; passada a
/// escolha, o app voltava a ser azul para as onze marcas. Aqui as seis entradas
/// OsAccent* do dicionário são trocadas em runtime — o mesmo mecanismo do
/// ThemeService, e pelo mesmo motivo (brush de dicionário nasce congelado).
///
/// ORDEM IMPORTA: o ThemeService NÃO escreve nenhuma chave OsAccent*, justamente
/// para não desfazer a marca ao alternar o tema. Quem troca o tema tem de chamar
/// os dois, nesta ordem:
///     ThemeService.Apply(escuro);
///     OsAccentService.Apply(_config.Os, escuro);
///
/// Legibilidade: cada derivado tem um contrato de contraste MEDIDO, não uma
/// suposição. Ver a tabela em docs/design-abas.md §3.6.
/// </summary>
public static class OsAccentService
{
    public static void Apply(TargetOs os, bool escuro)
    {
        var res = Application.Current?.Resources;
        if (res == null) return;

        var marca = OsCardVm.ToColor(OsCatalog.Get(os).Accent);
        var fundo = (res["CardBg"] as SolidColorBrush)?.Color ?? Colors.White;

        // Superfície preenchida: o branco (OnAccent) em cima dela precisa de 4,5:1.
        var cheia = Marca.Tinta(marca);
        // Texto/ícone: 5,5:1 contra o cartão do tema — a margem de 1 ponto é para o
        // caso em que este mesmo acento é escrito POR CIMA do véu suave.
        var texto = Ajustar(marca, fundo, 5.5, clarear: escuro);
        // Véu: alfa sobre a superfície do tema.
        var suave = Marca.Alfa(texto, escuro ? (byte)0x38 : (byte)0x24);
        // Para a barra lateral, que é escura NOS DOIS temas: a variante "acesa".
        var acesa = Marca.Legivel(marca, escuro: true);

        res["OsAccentColor"] = cheia;
        res["OsAccent"] = Congelado(new SolidColorBrush(cheia));
        res["OsAccentText"] = Congelado(new SolidColorBrush(texto));
        res["OsAccentSoft"] = Congelado(new SolidColorBrush(suave));
        res["OsAccentGradient"] = Congelado(new LinearGradientBrush(
            new GradientStopCollection { new(cheia, 0), new(Marca.Escalar(cheia, 1.22), 1) },
            new Point(0, 0), new Point(1, 1)));
        res["OsAccentWash"] = Congelado(new RadialGradientBrush(
            new GradientStopCollection
            {
                new(Marca.Alfa(texto, escuro ? (byte)0x26 : (byte)0x14), 0),
                new(Marca.Alfa(texto, 0x00), 1)
            })
        {
            GradientOrigin = new Point(0, 0),
            Center = new Point(0, 0),
            RadiusX = 0.8,
            RadiusY = 1.6
        });

        // A barra lateral usa gradiente escuro nos dois temas: seus tokens já
        // existem e não são temáveis. Aqui eles passam a ser DA MARCA — com a
        // variante "acesa" (luminância >= 0,32), que garante >= 6:1 sobre o
        // gradiente da barra por construção.
        res["SidebarAccent"] = Congelado(new SolidColorBrush(acesa));
        res["SidebarActiveBg"] = Congelado(new SolidColorBrush(Marca.Alfa(acesa, 0x2E)));
    }

    static Brush Congelado(Brush b) { b.Freeze(); return b; }

    /// <summary>
    /// Escurece (ou clareia) a marca até atingir a razão de contraste pedida contra
    /// o fundo indicado. Escalar os três canais preserva o matiz; misturar com
    /// branco/preto dessatura, então só entra quando escalar não chega lá — é o
    /// caso das marcas escuras e cinzentas (AlmaLinux #0F4266 no tema escuro).
    /// </summary>
    static Color Ajustar(Color cor, Color fundo, double alvo, bool clarear)
    {
        var r = cor;
        for (var i = 0; i < 80 && Marca.Contraste(r, fundo) < alvo; i++)
            r = Marca.Escalar(r, clarear ? 1.06 : 0.94);
        for (var i = 0; i < 40 && Marca.Contraste(r, fundo) < alvo; i++)
            r = Marca.Misturar(r, clarear ? Colors.White : Colors.Black, 0.08);
        return r;
    }
}
```

#### Chamadas

`MainWindow.Os.cs`, no início de `ApplyOsToUi()` (depois da linha 152):

```csharp
        // A cor da marca alimenta o Shell inteiro (cartoes, botoes, barra lateral,
        // fundo). Antes da UI: os DynamicResource ja resolvem no valor novo.
        OsAccentService.Apply(_config.Os, _config.AppDarkTheme);
```

`MainWindow.xaml.cs:445` (`ToggleTheme_Click`) e `1254` (`ApplyConfigToUi`), logo depois de
`ThemeService.Apply(...)`:

```csharp
        OsAccentService.Apply(_config.Os, _config.AppDarkTheme);
```

`App.xaml.cs:170`: deixar como está — o `MainWindow` chama `ApplyOsToUi()` no construtor e é
quem conhece o sistema.

#### 3.6.1 Migração de `{StaticResource Accent*}` → `{DynamicResource OsAccent*}`

`StaticResource` congela no parse e **não** seguiria a troca. Lista exaustiva:

**`Theme.xaml`** — 533 e 569 (`DropShadowEffect Color` → `{DynamicResource OsAccentColor}`);
548 · 598 · 798 · 998 · 1438 · 1441 · 1619 (`AccentSoft` → `OsAccentSoft`);
550 · 602 · 1660 (`AccentText` → `OsAccentText`);
562 · 599 · 839 · 844 · 1209 · 1287 (`AccentGradient` → `OsAccentGradient`);
597 · 702 · 799 · 1126 · 1538 · 1541 · 1544 (`Accent` → `OsAccent`);
1563 · 1566 (`ProgressGradient` → `OsAccentGradient`).
As linhas 910 · 943 · 1061 · 1062 · 1077 · 1151 · 1165 desaparecem nos templates novos (§3.3).

**`MainWindow.xaml`** — 87 (`AccentSoft`→`OsAccentSoft`) e 88 (`{StaticResource Accent}` →
`{DynamicResource OsAccentText}`; **isto corrige um defeito de contraste que já existe**: hoje é
o azul cheio sobre `AccentSoft`, ~3,5:1); 474/476, 806/808/809, 877, 957 pelo mesmo par.

#### 3.6.2 O fundo ambiente do Shell na cor do sistema

Hoje o `Backdrop` do Shell (`MainWindow.xaml:103`) usa o `AmbientSurface` com `Background` e
`BorderBrush` transparentes — ou seja, só as trilhas neutras aparecem. A tela de escolha, ao lado,
tinge tudo com a marca (`OsPickerView.xaml.cs:434-477`). Fazer o Shell repetir isso é o eco mais
barato e mais bonito entre as duas telas: o usuário escolhe o Ubuntu num fundo laranja e continua
num fundo laranja.

O `AmbientBackdrop` já lê três pincéis do próprio `Control` (`Theme.xaml:290-298`):
`Background` = wash A, `BorderBrush` = wash B, `Foreground` = cor das trilhas.

#### Arquivo NOVO: `Ui/BrandWash.cs`

```csharp
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IsoForge.Ui;

/// <summary>
/// Os três pincéis VIVOS que o template AmbientBackdrop consome (Theme.xaml):
/// dois washes radiais e a cor das trilhas. Vivos = criados em C# e nunca
/// congelados; um pincel vindo de ResourceDictionary é frozen e não aceita
/// ColorAnimation.
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
```

> `Marca` vive em `namespace IsoForge` (`OsPickerView.xaml.cs:20`); este arquivo está em
> `IsoForge.Ui`, então precisa de `using IsoForge;` — ou mova `Marca` para `IsoForge.Ui`
> num passo separado.

#### `MainWindow.Os.cs` — ligar

Campo, junto de `_ambient` (linha 24):

```csharp
    readonly Ui.BrandWash _fundoMarca = new();
```

Em `StartBackdrop()`, antes do `_ambient.Begin` (linha 35):

```csharp
            _fundoMarca.LigarEm(Backdrop);
```

E no fim de `ApplyOsToUi()`, junto da chamada de `OsAccentService`:

```csharp
        // O fundo do Shell recebe a MESMA marca do tabuleiro de escolha, a 45% da
        // intensidade: aqui o usuário lê formulários. É o que faz a passagem entre
        // as duas telas parecer uma só — o fundo não muda de cor no caminho.
        _fundoMarca.Tingir(OsCardVm.ToColor(os.Accent), _config.AppDarkTheme,
                           forca: 0.45, ms: _primeiroDesenho ? 0 : 480);
        _primeiroDesenho = false;
```

com `bool _primeiroDesenho = true;` ao lado do campo (na abertura não há transição a fazer).

---

## 4. Tokens novos

Inserir em `Theme.xaml` logo **depois** do `ProgressGradient` (linha 49) e **antes** do comentário
"LINGUAGEM VISUAL v2". Os valores abaixo são exatamente o azul de hoje: **antes de o
`OsAccentService` existir, nada muda de aparência.**

```xml
    <!-- ============================================================
         ACENTO DO SISTEMA ESCOLHIDO
         ============================================================
         Os valores aqui sao o PADRAO (o azul do IsoForge). Em runtime,
         OsAccentService.Apply(os, escuro) troca as seis entradas pela marca do
         sistema escolhido — derivadas de OsInfo.Accent com contraste MEDIDO
         (ver docs/design-abas.md, secao 3.6).

         Sempre por {DynamicResource}: {StaticResource} congela no parse e o
         controle ficaria azul para sempre.

         O ThemeService NAO escreve estas chaves. Quem troca o tema chama
         ThemeService.Apply e DEPOIS OsAccentService.Apply — senao a troca de
         tema apagaria a marca.
         ============================================================ -->
    <Color x:Key="OsAccentColor">#2563EB</Color>
    <SolidColorBrush x:Key="OsAccent"     Color="#2563EB"/>
    <SolidColorBrush x:Key="OsAccentText" Color="#1D4ED8"/>
    <SolidColorBrush x:Key="OsAccentSoft" Color="#242563EB"/>
    <LinearGradientBrush x:Key="OsAccentGradient" StartPoint="0,0" EndPoint="1,1">
        <GradientStop Color="#2563EB" Offset="0"/>
        <GradientStop Color="#4B87F5" Offset="1"/>
    </LinearGradientBrush>
    <RadialGradientBrush x:Key="OsAccentWash" GradientOrigin="0,0" Center="0,0" RadiusX="0.8" RadiusY="1.6">
        <GradientStop Color="#141D4ED8" Offset="0"/>
        <GradientStop Color="#001D4ED8" Offset="1"/>
    </RadialGradientBrush>
```

| Token | Tipo | Claro (padrão) | Escuro (padrão) | Em runtime |
|---|---|---|---|---|
| `OsAccentColor` | `Color` | `#2563EB` | `#2563EB` | `Marca.Tinta(marca)` |
| `OsAccent` | `SolidColorBrush` | `#2563EB` | `#2563EB` | `Marca.Tinta(marca)` |
| `OsAccentText` | `SolidColorBrush` | `#1D4ED8` | `#93B8FA` | `Ajustar(marca, CardBg, 5,5:1)` |
| `OsAccentSoft` | `SolidColorBrush` | `#242563EB` (14%) | `#3893B8FA` (22%) | `Alfa(OsAccentText, 0x24/0x38)` |
| `OsAccentGradient` | `LinearGradientBrush` | `#2563EB`→`#4B87F5` | idem | `cheia` → `Escalar(cheia, 1.22)` |
| `OsAccentWash` | `RadialGradientBrush` | `#141D4ED8`→transp. | `#2693B8FA`→transp. | `Alfa(texto, 0x14/0x26)` |

**Onde entra em `ThemeService.cs`:** *em lugar nenhum*, de propósito (razão no comentário acima).
O que muda no `ThemeService` é **nada**; o que muda são as duas chamadas que passam a acompanhá-lo
(`MainWindow.xaml.cs:445` e `:1254`).

**Tokens reaproveitados (não são novos, mas passam a ser escritos pelo `OsAccentService`):**
`SidebarAccent` e `SidebarActiveBg`. Os valores padrão de `Theme.xaml:159` e `:164` continuam
valendo; em runtime viram `Marca.Legivel(marca, escuro: true)` e o mesmo com alfa `0x2E` —
luminância ≥ 0,32 garante ≥ 6:1 sobre o gradiente da barra lateral, por construção.

---

## 5. Ordem de aplicação

Do maior retorno por menor risco ao mais invasivo. Cada passo compila e roda sozinho.

| # | Passo | Arquivos | Risco |
|---|---|---|---|
| **1** | **Tokens `OsAccent*`** (§4) — só acrescenta recursos com os valores de hoje | `Theme.xaml` (+22 linhas) | nulo |
| **2** | **Cascata de entrada** (§3.1) — `Ui/CardEntrance.cs` + novo `TabPageFlow` + `Play` no fim de `ApplyOsToUi` | 1 novo, `Theme.xaml`, `MainWindow.Os.cs` | baixo |
| **3** | **Cartão com relevo, véu e ícone** (§3.5) — novo template de `GroupBox` (+ `Tag` nos cartões, opcional) | `Theme.xaml`, `MainWindow.xaml` | baixo |
| **4** | **Direção da troca de aba** (§3.2) — `x:Name="TopbarTitle"` + 12 linhas em `MainTabs_SelectionChanged` | `MainWindow.xaml`, `MainWindow.xaml.cs` | baixo |
| **5** | **Micro-interações** (§3.3 a-f) — TextBox, PasswordBox, ComboBox, CheckBox, RadioButton, Button, Secondary, Expander, ListBoxItem | `Theme.xaml` | médio (9 templates) |
| **6** | **Ocupado / concluído** (§3.4) — chip de status, rodapé em `Grid`, `MainWindow.Feedback.cs`, ProgressBar com brilho, os dois `DesiredFrameRate` | 1 novo, `MainWindow.xaml`, `.xaml.cs`, `Theme.xaml` | médio |
| **7** | **`OsAccentService`** (§3.6) — o serviço + a migração `StaticResource`→`DynamicResource` (§3.6.1) | 1 novo, `Theme.xaml`, `MainWindow.*` | médio (36 trocas mecânicas) |
| **8** | **Fundo ambiente na marca** (§3.6.2) — `Ui/BrandWash.cs` + 4 linhas em `MainWindow.Os.cs` | 1 novo, `MainWindow.Os.cs` | baixo |
| **9** | **Revelação do Shell** (§5.1) — topbar, rodapé e abas entram quando a camada de escolha sai | `MainWindow.xaml`, `MainWindow.Os.cs` | baixo |
| **10** | **DataGrid + linhas** (§3.3g) — template simplificado de `DataGridRow` | `Theme.xaml` | **alto** — se quebrar, reverta só este bloco |

Passos 5 e 7 podem ser fatiados por controle: cada template é independente.

### 5.1 Passo 9 — revelação do Shell (código)

`MainWindow.xaml:67` → `<Border x:Name="TopBar" Grid.Column="1" Grid.Row="0" ...>`
`MainWindow.xaml` rodapé (o `Grid` de §3.4) → `<Grid x:Name="ActionRail" Grid.Column="1" Grid.Row="2">`

`MainWindow.Os.cs`, dentro de `HidePickerAsync`, no `some.Completed` (linha 412), antes de
`pronto.TrySetResult()`:

```csharp
            RevelarShell();
```

E o método:

```csharp
    /// <summary>
    /// O Shell aparece INTEIRO num quadro quando a camada de escolha sai. Aqui ele
    /// se monta: a topbar desce, o rodapé sobe e o trilho de abas entra em cascata
    /// — o mesmo gesto (e o mesmo easing) dos cartões do tabuleiro que acabou de
    /// sumir. É o que faz as duas telas parecerem um movimento só.
    /// </summary>
    void RevelarShell()
    {
        Entrar(TopBar, -14, 0, 260);
        Entrar(ActionRail, 16, 40, 280);

        var i = 0;
        foreach (var item in MainTabs.Items)
        {
            if (item is not TabItem aba || aba.Visibility != Visibility.Visible) continue;
            Entrar(aba, -10, 70 + i * 26, 240);
            i++;
        }
    }

    /// <summary>Um elemento entra deslizando, com atraso. Os transforms são
    /// reaproveitados: reabrir a escolha do sistema chama isto de novo.</summary>
    static void Entrar(FrameworkElement alvo, double de, int atrasoMs, int ms)
    {
        if (alvo.RenderTransform is not TranslateTransform t)
            alvo.RenderTransform = t = new TranslateTransform();

        var atraso = TimeSpan.FromMilliseconds(atrasoMs);
        alvo.Opacity = 0;                       // valor BASE: é ele que vale durante o atraso
        var surge = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { BeginTime = atraso };
        surge.Completed += (_, __) => { alvo.BeginAnimation(UIElement.OpacityProperty, null); alvo.Opacity = 1; };
        alvo.BeginAnimation(UIElement.OpacityProperty, surge);

        var desliza = new DoubleAnimation(de, 0, TimeSpan.FromMilliseconds(ms))
        { BeginTime = atraso, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        desliza.Completed += (_, __) => { t.BeginAnimation(TranslateTransform.YProperty, null); t.Y = 0; };
        t.BeginAnimation(TranslateTransform.YProperty, desliza);
    }
```

---

## 6. Como verificar

Regra geral de custo: com a janela em foco e **nada acontecendo**, o IsoForge tem de ficar em
**~1-3% de um núcleo** (só o `AmbientSb` a 20 fps). Confira no Gerenciador de Tarefas → Detalhes →
coluna CPU, com a janela maximizada a 2560×1440 e o app ocioso por 30 s. Depois clique noutra
janela: tem de cair para **~0%**.

| Passo | O que olhar | Sinal de que quebrou |
|---|---|---|
| 1 | Nada muda na tela. `dotnet build` limpo. | Erro de XAML: chave duplicada. |
| 2 | Abra cada aba: os cartões entram um a um, na diagonal, do canto superior esquerdo. Troque para Ubuntu: os cartões visíveis repetem a cascata sem buracos onde estavam os cartões do Windows. | Todos entram juntos (o `Stagger` não pegou) · a página pisca pronta antes de animar (`Opacity=0` não chegou a tempo) · um cartão fica invisível (animação não soltou o `Opacity`). |
| 3 | Passe o mouse por um cartão: sombra suave embaixo + borda mais forte, **sem** o cartão pular. Nos dois temas. Cartão sem `Tag`: cabeçalho idêntico ao de hoje. | Quadradinho vazio no cabeçalho (o `Trigger Tag = {x:Null}` não pegou) · sombra visível sempre. |
| 4 | ISO → Imagem Golden: o conteúdo entra **de baixo**. Golden → ISO: entra **de cima**. Clique 6 abas em 2 s: nada trava nem fica meio-transparente. | O eixo não inverte (a direção está sendo lida depois do `Loaded`). |
| 5 | Tab pelos campos: anel + trilho varrendo da esquerda; o texto **não anda meio pixel**. Abra um combo: a lista desce com corpo. Marque um checkbox: o azul entra por opacidade. Expander: o corpo desliza ao abrir e some antes de colapsar. | Campo "pula" ao focar (voltou o `BorderThickness`) · combo abre vazio (o `PART_EditableTextBox` foi perdido no template do ComboBox — confira `CmbTimezone`, que é `IsEditable`). |
| 6 | Parado: ponto verde imóvel. Clique "Gerar ISO": ponto azul pulsando + anel abrindo, barra com brilho correndo, texto pulsando a cada etapa. Ao terminar: visto verde que salta + varredura verde no topo do rodapé, que some deixando o fio do sistema. **Com a barra escondida, a CPU tem de voltar ao ocioso.** | O pulso continua depois de "Pronto" · a CPU fica em 8-15% com o app parado (algum loop sem `DesiredFrameRate` ou sem `StopStoryboard`). |
| 7 | Escolha Ubuntu: cartões, botões, checkboxes, item ativo da barra lateral e fio do rodapé em laranja. Fedora: azul-claro. AlmaLinux no tema escuro: o azul-marinho **clareia** e continua legível. Alterne o tema com o Ubuntu selecionado: **continua laranja** (é aqui que a ordem `ThemeService` → `OsAccentService` se prova). | Volta ao azul ao trocar o tema (ordem invertida) · algum controle continua azul (sobrou `StaticResource`) · texto de acento apagado no tema escuro (o `Ajustar` recebeu `clarear: false`). |
| 8 | Escolha Linux Mint e continue: o fundo do Shell tem o mesmo verde do tabuleiro, bem mais fraco. Troque de sistema pelo botão da topbar: o fundo faz a transição em ~0,5 s, sem pular. | O fundo fica cinza (os pincéis não foram ligados antes do `Begin`) · exceção "Cannot animate ... immutable object" (algum pincel veio congelado do dicionário). |
| 9 | Reinicie o app, escolha um sistema: a topbar desce, o rodapé sobe, as abas entram uma a uma e a página faz a cascata. Volte a "trocar sistema" e confirme: repete sem resíduo. | Algum elemento fica em `Opacity` 0 (a animação não soltou) · a segunda passagem não anima (transform empilhado). |
| 10 | Personalização → "Nome automático por unidade": as duas colunas aparecem, dá para digitar e adicionar linha; a linha selecionada tem barra à esquerda. Idem em Aplicativos → FortiClient → túneis manuais. | Grade em branco ou sem colunas: reverta este bloco — o template simplificado não serve para a configuração que a grade passou a usar. |

### Verificação por render offscreen

O harness de preview registrado em `MEMORY.md` (`ui-preview-harness`) cobre os passos 1, 3, 7 e 8
(tudo que é estado estático). **Não cobre** 2, 4, 6 e 9: sem janela na tela não há relógio de
composição, e as animações ficam paradas no primeiro quadro. Para esses, use a mesma saída que o
`OsPickerView.PreviewSeek` (`OsPickerView.xaml.cs:673-680`) usa —
`SeekAlignedToLastTick` — ou verifique com o app aberto de verdade.

---

## Apêndice — decisões em que fiquei dividido

1. **Animar a saída da aba.** Descartei: exigiria adiar a troca (prejudica quem clica rápido) ou
   fotografar a árvore com `VisualBrush` (caro e borra texto). A direção da entrada carrega o eixo
   sozinha. Se depois quiser a saída, o caminho é um `VisualBrush` congelado num `Image` sobre o
   `ContentPresenter` — mas só vale se o app parar de rodar em máquinas de 1366×768.
2. **Lift no hover do cartão.** O tabuleiro tem; aqui tirei. Um cartão que sobe 3 px enquanto se
   digita dentro dele é ruído, não feedback. Se ficar parado demais, o meio-termo é lift só quando
   `IsKeyboardFocusWithin` for `False`.
3. **`FocusRing` seguir a marca.** Não segue. É o único lugar em que preferi previsibilidade a
   beleza; num Fedora azul, foco + seleção + acento na mesma cor apagam três estados de uma vez.
4. **Brilho da `ProgressBar` por `PointAnimation` no gradiente** em vez de `TranslateTransform`:
   escolhi o gradiente porque as coordenadas são relativas à caixa e a mesma animação serve para a
   barra de 130 px e para a de 900. O preço é depender do `x:Name` no pincel para ele não nascer
   congelado — padrão que já funciona neste `Theme.xaml` (`x:Name="bg"` no botão).
