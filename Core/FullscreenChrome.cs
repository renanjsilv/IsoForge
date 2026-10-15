namespace IsoForge.Core;

/// <summary>
/// O fundo animado compartilhado pelas telas em TELA CHEIA do provisionamento
/// (seleção de unidade e progresso da instalação).
///
/// Existe para as duas serem a MESMA tela do ponto de vista de quem olha: o operador
/// escolhe a unidade, a máquina reinicia, e o que aparece depois tem de parecer a
/// continuação — não outro programa. Um bloco duplicado à mão nos dois geradores
/// garantiria que na primeira mudança um deles divergisse.
///
/// Deliberadamente LEVE: estas telas rodam durante a instalação de programas, numa
/// máquina recém-formatada que muitas vezes ainda está no driver de vídeo genérico —
/// ali o WPF cai para renderização por software. Dois claroes radiais que derivam,
/// a 24 fps, e nada mais. Sem arcos gigantes como no app.
/// </summary>
public static class FullscreenChrome
{
    /// <summary>Cor de fundo das telas cheias (a mesma nos dois geradores).</summary>
    public const string Fundo = "#0B1220";

    /// <summary>
    /// Os elementos do fundo. Vão como PRIMEIRO filho do Grid raiz.
    /// <paramref name="rowSpan"/> deve cobrir todas as linhas do Grid que os hospeda.
    /// </summary>
    public static string Camada(int rowSpan) => $"""
        <Canvas x:Name="FundoVivo" Grid.RowSpan="{rowSpan}" IsHitTestVisible="False">
          <Ellipse Width="1100" Height="1100" Canvas.Left="-320" Canvas.Top="-420" Opacity="0.55">
            <Ellipse.Fill>
              <RadialGradientBrush>
                <GradientStop Color="#332563EB" Offset="0"/>
                <GradientStop Color="#002563EB" Offset="1"/>
              </RadialGradientBrush>
            </Ellipse.Fill>
            <Ellipse.RenderTransform><TranslateTransform x:Name="GlowA"/></Ellipse.RenderTransform>
          </Ellipse>
          <Ellipse Width="1300" Height="1300" Canvas.Left="900" Canvas.Top="360" Opacity="0.42">
            <Ellipse.Fill>
              <RadialGradientBrush>
                <GradientStop Color="#2A38BDF8" Offset="0"/>
                <GradientStop Color="#0038BDF8" Offset="1"/>
              </RadialGradientBrush>
            </Ellipse.Fill>
            <Ellipse.RenderTransform><TranslateTransform x:Name="GlowB"/></Ellipse.RenderTransform>
          </Ellipse>
        </Canvas>
""";

    /// <summary>
    /// O gatilho que põe o fundo em movimento. Tem de vir como PROPRIEDADE do Grid,
    /// antes de qualquer filho: propriedade de elemento depois de conteúdo faz o
    /// XamlReader recusar com "a propriedade Children já foi definida em Grid" — foi
    /// exatamente o erro que derrubou a primeira versão desta tela.
    /// </summary>
    public static string Gatilho(bool comAnel) => $"""
        <Grid.Triggers>
          <EventTrigger RoutedEvent="FrameworkElement.Loaded">
            <BeginStoryboard>
              <Storyboard Timeline.DesiredFrameRate="24">
{(comAnel ? """
                <DoubleAnimation Storyboard.TargetName="AnelGiro" Storyboard.TargetProperty="Angle"
                                 From="0" To="360" Duration="0:0:2.4" RepeatBehavior="Forever"/>
""" : "")}                <DoubleAnimation Storyboard.TargetName="GlowA" Storyboard.TargetProperty="Y"
                                 From="-70" To="110" Duration="0:0:42" AutoReverse="True" RepeatBehavior="Forever"/>
                <DoubleAnimation Storyboard.TargetName="GlowA" Storyboard.TargetProperty="X"
                                 From="-50" To="90" Duration="0:0:33" AutoReverse="True" RepeatBehavior="Forever"/>
                <DoubleAnimation Storyboard.TargetName="GlowB" Storyboard.TargetProperty="Y"
                                 From="60" To="-120" Duration="0:0:51" AutoReverse="True" RepeatBehavior="Forever"/>
                <DoubleAnimation Storyboard.TargetName="GlowB" Storyboard.TargetProperty="X"
                                 From="40" To="-110" Duration="0:0:38" AutoReverse="True" RepeatBehavior="Forever"/>
              </Storyboard>
            </BeginStoryboard>
          </EventTrigger>
        </Grid.Triggers>
""";
}
