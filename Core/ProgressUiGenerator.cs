using System.IO;
using System.Text;
using IsoForge.Models;

namespace IsoForge.Core;

/// <summary>
/// Gera o ShowProgress.ps1: a tela CHEIA que acompanha a instalação dos programas no
/// primeiro logon — ícone do programa em cima, barra de progresso e a contagem do que
/// falta — no lugar do terminal preto rolando o script.
///
/// COMO SE ENCAIXA (a ordem importa e não é a óbvia): quem manda é o
/// <c>install.cmd</c>. Ele dispara esta tela e segue instalando; a tela é só um
/// VISUALIZADOR que lê um arquivo de status. Se a tela morrer, a instalação continua
/// — no arranjo inverso (a tela chamando o install) uma falha de interface abortaria
/// o provisionamento inteiro.
///
/// O status trafega por <c>%ProgramData%\IsoForge\progress.txt</c>, uma linha só:
///     etapa|indice|total|rotulo
/// com etapa em {fase, app, fim}. É deliberadamente burro: arquivo de texto sobrevive
/// a reinício de processo, não precisa de permissão especial e é trivial de depurar
/// numa máquina recém-formatada.
///
/// SEM TEMPO ESTIMADO, de propósito. A tela já teve um "faltam cerca de N min" e ele
/// errava demais: dependia de pesos que são chutes (quanto cada programa demora) e de
/// uma taxa recalibrada com pouquíssimas amostras, então numa máquina lenta ou com um
/// programa atípico o número saltava e fazia a tela parecer quebrada. Um número errado
/// é pior que número nenhum. No lugar dele vai a contagem do que falta, que não erra.
///
/// A BARRA continua por PESO (ver <see cref="Peso"/>) e não por contagem: instalar o
/// Office demora dezenas de vezes mais que o 7-Zip, e uma barra que anda em saltos
/// iguais mentiria sobre o andamento. Como o status só diz qual programa começou, o
/// avanço DENTRO do programa é interpolado pelo tempo — sem isso a barra ficava parada
/// durante o programa inteiro.
/// </summary>
public static class ProgressUiGenerator
{
    public const string FileName = "ShowProgress.ps1";

    /// <summary>Pasta de ícones dentro da ISO, relativa a Setup\Apps.</summary>
    public const string IconsSubFolder = "Icons";

    /// <summary>
    /// UTF-8 COM BOM, de proposito. Sem BOM o Windows PowerShell 5.1 le o arquivo como
    /// ANSI e todo caractere acentuado chega corrompido. O gerador da tela de unidade ja
    /// gravava com BOM por esse motivo; este gravava sem, e saia com 4 travessoes
    /// corrompidos. Hoje sao so comentarios, mas a primeira string acentuada que
    /// entrasse aqui quebraria em producao sem aviso.
    /// </summary>
    public static void WriteTo(BuildConfig cfg, string path) =>
        File.WriteAllText(path, Generate(cfg), new UTF8Encoding(true));

    public static string Generate(BuildConfig cfg)
    {
        var apps = cfg.Apps.Where(a => !string.IsNullOrWhiteSpace(a.InstallerPath)).ToList();

        // Lista de programas com peso e ícone. O peso é calculado na GERAÇÃO, onde os
        // instaladores estão em mãos — na máquina de destino não haveria como saber.
        var itens = new StringBuilder();
        for (var i = 0; i < apps.Count; i++)
        {
            var a = apps[i];
            var nome = a.Name.Replace("'", "''");
            itens.AppendLine(
                $"    [pscustomobject]@{{ Nome = '{nome}'; Peso = {Peso(a, cfg).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}; " +
                $"Icone = '{i + 1}.png'; Glifo = [char]{Glifo(a)} }},");
        }

        return Corpo
            // Fundo e gatilho vem do FullscreenChrome, nao copiados aqui: a tela de
            // unidade usa os mesmos, e as duas precisam parecer a MESMA tela.
            .Replace("{{FUNDO}}", FullscreenChrome.Camada(3))
            .Replace("{{GATILHO}}", FullscreenChrome.Gatilho(comAnel: true))
            .Replace("{{ITENS}}", itens.ToString())
            .Replace("{{ICONDIR}}", InstallScriptGenerator.AppsDirOnDisk + "\\" + IconsSubFolder)
            .Replace("{{TITULO}}", cfg.IsLinux ? "Preparando o sistema" : "Preparando o Windows")
            .Replace("{{REINICIA}}", cfg.SandboxTest ? "$false" : "$true")
            // A pagina de unidade so existe quando a escolha acontece no 1o logon. No modo
            // de auditoria quem mostra a tela e o SelectUnit.ps1, antes de o usuario existir.
            .Replace("{{COMUNIDADE}}", ComUnidade(cfg) ? "$true" : "$false")
            .Replace("{{UNIDADES}}", UnitSelectorGenerator.ArrayUnidades(cfg))
            .Replace("{{GETHOSTNAME}}", UnitSelectorGenerator.FuncaoGetHostname)
            .Replace("{{RENOMEAR}}", UnitSelectorGenerator.FuncaoRenomear)
            .Replace("{{PAGINAUNIDADE}}", PaginaUnidade);
    }

    /// <summary>
    /// A escolha da unidade acontece DENTRO desta tela?
    ///
    /// So no 1o logon com conta local. No modo de auditoria e no Entra ID a escolha
    /// acontece em outro momento do provisionamento (antes de o usuario existir, ou por
    /// uma tarefa SYSTEM), e ali o SelectUnit.ps1 continua sendo a tela.
    /// </summary>
    internal static bool ComUnidade(BuildConfig c) =>
        c.FullscreenProgress
        && c.UseUnitSelection
        && c.UnitMethod == UnitSelectionMethod.FirstLogon
        && c.Mode == DeploymentMode.LocalAccount;

    /// <summary>
    /// Peso do programa na barra, em SEGUNDOS ESTIMADOS de instalação.
    ///
    /// Antes o peso era o tamanho do instalador em MB, e isso errava feio justamente
    /// no programa que mais demora: o Office entra como um <c>setup.exe</c> de ~7 MB
    /// (o ODT é só o iniciador) e instala 3,6 GB durante 10 a 20 minutos. Numa lista
    /// com Chrome, Reader e Office, o Office pesava menos que o Reader — a barra
    /// passava correndo pelo começo e depois ficava parada o resto do provisionamento.
    ///
    /// Os números abaixo são ESTIMATIVAS, não medições. Não precisam ser exatos: a
    /// tela recalibra a taxa a cada programa concluído, então o erro inicial se
    /// corrige sozinho ao longo da execução. O que eles precisam é acertar a ORDEM DE
    /// GRANDEZA entre um programa e outro, e isso o tamanho do instalador não fazia.
    /// </summary>
    static double Peso(AppEntry a, BuildConfig cfg)
    {
        if (a.Kind == AppKind.Office)
        {
            // Do payload real quando ele está em mãos; senão, um Office x64 com um idioma.
            var gb = 3.6;
            try
            {
                var v = OfficeSource.Validar(cfg.OfficeSourceFolder ?? "");
                if (v.gb > 0.5) gb = v.gb;
            }
            catch { }
            // Offline: só descompactar e registrar. Online: ainda tem o download.
            return cfg.OfficeOffline ? 120 + gb * 180 : 180 + gb * 400;
        }

        double mb = 0;
        try { if (File.Exists(a.InstallerPath)) mb = new FileInfo(a.InstallerPath).Length / 1024.0 / 1024.0; }
        catch { }
        return 20 + mb * 1.2;
    }

    /// <summary>
    /// Glifo de reserva (Segoe MDL2 Assets, presente em todo Windows 10/11) para
    /// quando o ícone não pôde ser extraído do instalador — MSI, por exemplo, quase
    /// sempre devolve o ícone genérico do Windows Installer.
    /// </summary>
    static string Glifo(AppEntry a) => a.Kind switch
    {
        AppKind.Office => "0xE8A5",   // documento
        _              => "0xE896",   // download
    };


    /// <summary>
    /// A página 1: a escolha da unidade, como um Grid dentro da MESMA janela da página
    /// de progresso. É a mesma marcação da tela do modo de auditoria
    /// (<see cref="UnitSelectorGenerator"/>), só que sem o &lt;Window&gt; em volta.
    /// </summary>
    const string PaginaUnidade = """
    <Grid x:Name="PagUnidade">
      <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="*"/>
        <RowDefinition Height="Auto"/>
      </Grid.RowDefinitions>

      <StackPanel Grid.Row="0" HorizontalAlignment="Center" Margin="0,70,0,20">
        <TextBlock Text="Selecione a unidade" Foreground="White" FontSize="42" FontWeight="SemiBold" HorizontalAlignment="Center"/>
        <TextBlock Text="Isso define o nome deste computador" Foreground="#94A3B8" FontSize="18" HorizontalAlignment="Center" Margin="0,8,0,0"/>
      </StackPanel>

      <ItemsControl x:Name="ListaUnidades" Grid.Row="1" HorizontalAlignment="Center" VerticalAlignment="Center" Width="900">
        <ItemsControl.ItemsPanel>
          <ItemsPanelTemplate><WrapPanel HorizontalAlignment="Center"/></ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
          <DataTemplate>
            <Button Width="270" Height="120" Margin="14" Cursor="Hand"
                    Tag="{Binding Prefixo}" Content="{Binding Nome}"
                    Foreground="White" FontSize="24" FontWeight="SemiBold" BorderThickness="0">
              <Button.Style>
                <Style TargetType="Button">
                  <Setter Property="Background" Value="#1E293B"/>
                  <Setter Property="Template">
                    <Setter.Value>
                      <ControlTemplate TargetType="Button">
                        <Border Background="{TemplateBinding Background}" CornerRadius="16" TextElement.Foreground="White">
                          <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                      </ControlTemplate>
                    </Setter.Value>
                  </Setter>
                  <Style.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter Property="Background" Value="#2563EB"/>
                    </Trigger>
                  </Style.Triggers>
                </Style>
              </Button.Style>
            </Button>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>

      <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,20,0,60">
        <TextBlock Text="Ou nome personalizado:" Foreground="#94A3B8" FontSize="16" VerticalAlignment="Center" Margin="0,0,10,0"/>
        <TextBox x:Name="TxtCustom" Width="240" Height="36" FontSize="16" VerticalContentAlignment="Center"/>
        <Button x:Name="BtnCustom" Content="Usar" Width="90" Height="36" Margin="10,0,0,0"
                Background="#334155" Foreground="White" BorderThickness="0" FontSize="16" Cursor="Hand"/>
      </StackPanel>
    </Grid>
""";

    // O corpo é literal: nada de interpolação de C# aqui dentro, para o $ do
    // PowerShell não precisar de escape. Os pontos variáveis são os {{marcadores}}.
    const string Corpo = """
# ============================================================================
# IsoForge - tela de progresso da instalacao (primeiro logon)
# ============================================================================
# VISUALIZADOR: quem instala e o install.cmd, que dispara esta tela e segue. Aqui
# so lemos o arquivo de status e desenhamos. Qualquer falha nesta tela NAO pode
# parar o provisionamento, por isso tudo roda dentro de try/catch e o pior caso e
# a tela fechar sozinha.
# ============================================================================

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$Pasta      = Join-Path $env:ProgramData 'IsoForge'
try { New-Item -ItemType Directory -Force $Pasta | Out-Null } catch {}
$StatusFile = Join-Path $Pasta 'progress.txt'
# Arquivo que o install.cmd fica esperando: assim que ele aparece, a unidade foi
# escolhida e o provisionamento segue. E o unico ponto de encontro entre os dois.
$UnitFile   = Join-Path $Pasta 'unidade.txt'
$LogUi      = Join-Path $Pasta 'ui.log'

function Reg([string]$msg) {
    # TODA falha desta tela vira uma linha aqui. Os try/catch mudos de antes faziam a
    # tela parar de atualizar sem deixar rastro: na maquina de destino restava adivinhar
    # por que ela ficava em "Preparando o Windows" enquanto o Office instalava atras.
    try { Add-Content -LiteralPath $LogUi -Value ("{0:HH:mm:ss}  {1}" -f (Get-Date), $msg) } catch {}
}

# A escolha da unidade acontece NESTA tela?
$ComUnidade = {{COMUNIDADE}}

# Primeira linha do log, SEMPRE. Sem ela, uma falha antes da primeira troca de pagina
# nao deixava rastro nenhum e a tela parecia simplesmente nao funcionar.
Reg "tela iniciada (unidade=$ComUnidade reinicia={{REINICIA}})"

# Mesmo cuidado do $Programas acima: com uma unidade so isto viraria objeto solto.
$Unidades = @(@(
{{UNIDADES}}    $null
) | Where-Object { $_ })

{{GETHOSTNAME}}

{{RENOMEAR}}
# A maquina reinicia ao terminar? No teste do Sandbox nao, e a tela precisa
# saber disso para poder sair de cena em vez de ficar travada na frente de tudo.
$ReiniciaNoFim = {{REINICIA}}
$IconDir    = '{{ICONDIR}}'

# O @( ) EXTERNO NAO E ENFEITE.
#
# "@(...) | Where-Object" devolve um OBJETO SOLTO quando sobra um unico item, e
# .Count num PSCustomObject e $null. Com um programa so na lista dava:
#     $indice -ge $null  ->  0 -ge 0  ->  verdadeiro  ->  $indice = $null - 1 = -1
#     -1 -ne -1  ->  falso  ->  o bloco que escreve o NOME nunca rodava
# A tela ficava presa em "Preparando o sistema" com 0% enquanto o programa instalava
# atras. Medido no Sandbox com uma ISO de um app so. O @( ) de fora garante array.
$Programas = @(@(
{{ITENS}}    $null
) | Where-Object { $_ })

$PesoTotal = 0.0
foreach ($p in $Programas) { $PesoTotal += [double]$p.Peso }
if ($PesoTotal -le 0) { $PesoTotal = 1.0 }

[xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" WindowState="Maximized" Topmost="True" ResizeMode="NoResize"
        Background="#0B1220" FontFamily="Segoe UI Variable, Segoe UI">
  <Grid x:Name="Raiz" ClipToBounds="True">
{{GATILHO}}

{{FUNDO}}

    <!-- =============== PAGINA 1: ESCOLHA DA UNIDADE ===============
         Mesma JANELA da pagina de progresso, de proposito. Antes eram dois
         processos PowerShell disputando a tela: o de progresso subia com
         Topmost e um temporizador que reativava a janela a cada 2 s, e cobria
         a escolha da unidade - que ficava atras esperando um clique que nunca
         chegava. A maquina parecia travada e so saia disso alternando janela
         pelo menu Iniciar. Com uma janela so nao ha o que disputar. -->
{{PAGINAUNIDADE}}

    <!-- =============== PAGINA 2: PROGRESSO DA INSTALACAO =============== -->
    <Grid x:Name="PagProgresso" Visibility="Collapsed">
      <Grid.RowDefinitions>
        <RowDefinition Height="*"/>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="Auto"/>
      </Grid.RowDefinitions>

      <!-- ================= CENTRO: icone + nome ================= -->
      <StackPanel Grid.Row="0" HorizontalAlignment="Center" VerticalAlignment="Center">
        <Grid Width="200" Height="200" HorizontalAlignment="Center">
          <!-- anel que gira: o unico movimento continuo perto do texto, e o que diz
               "esta trabalhando" sem prometer um progresso que nao temos -->
          <Ellipse x:Name="Anel" Width="184" Height="184" StrokeThickness="3"
                   Stroke="#3B82F6" StrokeDashArray="58.9 133.8" StrokeDashCap="Round"
                   Opacity="0.85" RenderTransformOrigin="0.5,0.5">
            <Ellipse.RenderTransform><RotateTransform x:Name="AnelGiro"/></Ellipse.RenderTransform>
          </Ellipse>
          <Border Width="132" Height="132" CornerRadius="30" Background="#16213A"
                  BorderBrush="#24344F" BorderThickness="1">
            <Grid>
              <!-- MARCA DA FASE DE PREPARO. Antes o quadro ficava VAZIO enquanto a tela
                   dizia "Preparando o Windows": um retangulo escuro no meio de uma tela
                   escura, que parecia imagem que nao carregou. Sao quatro quadrados em
                   2x2 desenhados como VETOR, nao um glifo de fonte: nao depende de
                   nenhuma fonte estar instalada na maquina recem-formatada. -->
              <Grid x:Name="MarcaPrep" Width="60" Height="60"
                    HorizontalAlignment="Center" VerticalAlignment="Center">
                <Grid.RowDefinitions><RowDefinition/><RowDefinition/></Grid.RowDefinitions>
                <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
                <Rectangle Grid.Row="0" Grid.Column="0" Fill="#7FB0FF" RadiusX="3" RadiusY="3" Margin="0,0,3,3"/>
                <Rectangle Grid.Row="0" Grid.Column="1" Fill="#5B92E8" RadiusX="3" RadiusY="3" Margin="3,0,0,3"/>
                <Rectangle Grid.Row="1" Grid.Column="0" Fill="#5B92E8" RadiusX="3" RadiusY="3" Margin="0,3,3,0"/>
                <Rectangle Grid.Row="1" Grid.Column="1" Fill="#7FB0FF" RadiusX="3" RadiusY="3" Margin="3,3,0,0"/>
              </Grid>
              <Image x:Name="Icone" Width="76" Height="76" Stretch="Uniform" Visibility="Collapsed"/>
              <TextBlock x:Name="Glifo" FontFamily="Segoe MDL2 Assets" FontSize="56"
                         Foreground="#7FB0FF" HorizontalAlignment="Center"
                         VerticalAlignment="Center" Visibility="Collapsed"/>
            </Grid>
          </Border>
        </Grid>

        <TextBlock x:Name="Nome" Text="{{TITULO}}" Foreground="White" FontSize="40"
                   FontWeight="SemiBold" HorizontalAlignment="Center" Margin="0,30,0,0"/>
        <TextBlock x:Name="Sub" Text="preparando" Foreground="#93A7C4" FontSize="17"
                   HorizontalAlignment="Center" Margin="0,10,0,0"/>
      </StackPanel>

      <!-- ================= BARRA ================= -->
      <StackPanel Grid.Row="1" HorizontalAlignment="Center" Margin="0,0,0,34">
        <Grid Width="760" Height="12">
          <Border CornerRadius="6" Background="#16213A"/>
          <Border x:Name="Barra" CornerRadius="6" HorizontalAlignment="Left" Width="0">
            <Border.Background>
              <LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
                <GradientStop Color="#2563EB" Offset="0"/>
                <GradientStop Color="#60A5FA" Offset="1"/>
              </LinearGradientBrush>
            </Border.Background>
          </Border>
        </Grid>
        <Grid Width="760" Margin="0,12,0,0">
          <TextBlock x:Name="Pct" Text="0%" Foreground="#DCE7F7" FontSize="15"
                     FontWeight="SemiBold" HorizontalAlignment="Left"/>
          <!-- No lugar da estimativa de tempo: a CONTAGEM. A estimativa dependia de
               pesos que sao chutes (quanto cada programa demora) e de uma taxa
               recalibrada com poucas amostras; em maquina lenta ou com um programa
               atipico ela errava feio e fazia a tela parecer quebrada. Contar o que
               falta nao erra. -->
          <TextBlock x:Name="Restantes" Text="" Foreground="#93A7C4"
                     FontSize="15" HorizontalAlignment="Right"/>
        </Grid>
      </StackPanel>

      <!-- ================= LISTA DE ETAPAS ================= -->
      <ItemsControl x:Name="Lista" Grid.Row="2" HorizontalAlignment="Center" Margin="0,0,0,54">
        <ItemsControl.ItemsPanel>
          <ItemsPanelTemplate><WrapPanel HorizontalAlignment="Center" MaxWidth="1200"/></ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
          <DataTemplate>
            <Border CornerRadius="9" Padding="13,7" Margin="5" Background="{Binding Fundo}">
              <StackPanel Orientation="Horizontal">
                <TextBlock Text="{Binding Marca}" FontFamily="Segoe MDL2 Assets" FontSize="12"
                           Foreground="{Binding Cor}" VerticalAlignment="Center" Margin="0,0,8,0"/>
                <TextBlock Text="{Binding Nome}" FontSize="14" Foreground="{Binding Cor}"
                           VerticalAlignment="Center"/>
              </StackPanel>
            </Border>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>

    </Grid>
  </Grid>
</Window>
"@

$reader = New-Object System.Xml.XmlNodeReader $xaml
$win    = [Windows.Markup.XamlReader]::Load($reader)

$uiIcone = $win.FindName('Icone')
$uiGlifo = $win.FindName('Glifo')
$uiNome  = $win.FindName('Nome')
$uiSub   = $win.FindName('Sub')
$uiBarra = $win.FindName('Barra')
$uiPct   = $win.FindName('Pct')
$uiRest  = $win.FindName('Restantes')
$uiLista = $win.FindName('Lista')
$uiMarca = $win.FindName('MarcaPrep')
$uiPagU  = $win.FindName('PagUnidade')
$uiPagP  = $win.FindName('PagProgresso')
$uiUnid  = $win.FindName('ListaUnidades')
$uiTxt   = $win.FindName('TxtCustom')
$uiBtn   = $win.FindName('BtnCustom')

# Se algum elemento nao veio, a tela nao tem como funcionar: melhor registrar QUAL
# faltou do que ficar atualizando nada em silencio.
foreach ($par in @{ Nome=$uiNome; Sub=$uiSub; Barra=$uiBarra; Pct=$uiPct; Restantes=$uiRest;
                    Lista=$uiLista; PagProgresso=$uiPagP }.GetEnumerator()) {
    if ($null -eq $par.Value) { Reg "FALTOU o elemento $($par.Key) no XAML" }
}

# ---------------------------------------------------------------- lista de etapas
# Reconstruida a cada mudanca de indice. Sao poucos itens (2 a 10), entao redesenhar
# custa menos que manter binding de colecao observavel dentro de um script.
function Atualizar-Lista([int]$atual) {
    $itens = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $Programas.Count; $i++) {
        if ($i -lt $atual)      { $marca = [char]0xE73E; $cor = '#4ADE80'; $fundo = '#14304A2A' }
        elseif ($i -eq $atual)  { $marca = [char]0xE768; $cor = '#DCE7F7'; $fundo = '#242563EB' }
        else                    { $marca = [char]0xE9CE; $cor = '#6B7F9B'; $fundo = '#12203A' }
        [void]$itens.Add([pscustomobject]@{
            Nome = $Programas[$i].Nome; Marca = $marca; Cor = $cor; Fundo = $fundo
        })
    }
    $uiLista.ItemsSource = $itens
}

function Mostrar-Icone([int]$indice) {
    $ok = $false
    try {
        $arq = Join-Path $IconDir $Programas[$indice].Icone
        if (Test-Path $arq) {
            $bmp = New-Object System.Windows.Media.Imaging.BitmapImage
            $bmp.BeginInit()
            $bmp.UriSource = New-Object System.Uri($arq)
            $bmp.CacheOption = 'OnLoad'
            $bmp.EndInit()
            $uiIcone.Source = $bmp
            $uiIcone.Visibility = 'Visible'
            $uiGlifo.Visibility = 'Collapsed'
            $uiMarca.Visibility = 'Collapsed'
            $ok = $true
        }
    } catch { }

    if (-not $ok) {
        # Sem PNG extraido (MSI costuma nao ter icone proprio): cai no glifo.
        $uiIcone.Visibility = 'Collapsed'
        $uiGlifo.Text = $Programas[$indice].Glifo
        $uiGlifo.Visibility = 'Visible'
        $uiMarca.Visibility = 'Collapsed'
    }
}

# ---------------------------------------------------------------- relogio
# O relogio do PROGRESSO comeca no primeiro programa, nao no inicio do script: entre
# um e outro esta a selecao de unidade, que espera uma pessoa. Contar aquele tempo
# fazia a taxa nascer errada e a estimativa mentir pelo resto da instalacao.
$script:inicioApps = $null
$script:inicioApp  = $null
$script:ultIndice  = -1
# Segundos por unidade de peso. O peso JA vem em segundos estimados, entao 1.0 e o
# palpite inicial; a cada programa concluido a taxa real substitui o palpite.
$script:taxa = 1.0

$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromMilliseconds(700)

$timer.Add_Tick({
    try {
        if (-not (Test-Path $StatusFile)) { return }

        $linha = (Get-Content $StatusFile -ErrorAction Stop -Raw).Trim()
        if ([string]::IsNullOrWhiteSpace($linha)) { return }

        $campos = $linha.Split('|')
        $etapa  = $campos[0]
        if (-not $script:leuAlgum) { $script:leuAlgum = $true; Reg "primeiro status lido: $linha" }

        if ($etapa -eq 'fim') {
            $uiNome.Text = 'Tudo pronto'
            $uiIcone.Visibility = 'Collapsed'
            $uiGlifo.Text = [char]0xE73E
            $uiGlifo.Visibility = 'Visible'
            $uiBarra.Width = 760
            $uiPct.Text = '100%'
            $uiRest.Text = ''
            Atualizar-Lista $Programas.Count
            $timer.Stop()
            $script:terminou = $true

            if ($ReiniciaNoFim) {
                # Na maquina real a tela FICA ate o reinicio: fechar aqui devolveria a
                # area de trabalho por alguns segundos, que e o que se quer evitar.
                $uiSub.Text = 'reiniciando em instantes'
            } else {
                # No teste (Sandbox) nao ha reinicio. Sem esta saida a tela ficava
                # coberta a tela inteira, sem botao e sem fechar, dizendo "reiniciando
                # em instantes" para sempre — parecia que o provisionamento tinha
                # travado no fim quando na verdade ele tinha TERMINADO.
                $uiSub.Text = 'teste concluido - fechando em 10 s (Esc fecha agora)'
                # $script: de proposito. Uma variavel LOCAL criada aqui dentro nao e
                # enxergada pelo scriptblock do proprio Add_Tick: $fechar chegava la como
                # $null, o .Stop() lancava, o try abortava e o $win.Close() NUNCA rodava —
                # a tela continuava presa exatamente como antes. Medido rodando o tick
                # sob um Dispatcher.Run de verdade; a checagem por texto nao pegava.
                $script:fechar = New-Object System.Windows.Threading.DispatcherTimer
                $script:fechar.Interval = [TimeSpan]::FromSeconds(10)
                $script:fechar.Add_Tick({
                    # fecha PRIMEIRO: se o Stop falhar por qualquer motivo, a janela
                    # ja saiu de cena, que e o que importa.
                    try { $win.Close() } catch { }
                    try { $script:fechar.Stop() } catch { }
                })
                $script:fechar.Start()
            }
            return
        }

        if ($etapa -eq 'fase') {
            $uiNome.Text = $campos[3]
            $uiSub.Text  = 'preparando o sistema'
            return
        }

        # etapa = app -> indice|total|rotulo
        $indice = [int]$campos[1] - 1
        if ($indice -lt 0) { $indice = 0 }
        if ($indice -ge $Programas.Count) { $indice = $Programas.Count - 1 }

        if ($indice -ne $script:ultIndice) {
            # Recalibra a taxa com o que ja foi medido nesta maquina (uma maquina lenta
            # ou um SSD rapido mudam tudo). Limitada para um programa atipico nao
            # estragar a estimativa dos outros.
            if ($script:inicioApps -ne $null -and $indice -gt 0) {
                $pesoFeito = 0.0
                for ($i = 0; $i -lt $indice; $i++) { $pesoFeito += [double]$Programas[$i].Peso }
                if ($pesoFeito -gt 0) {
                    $t = ((Get-Date) - $script:inicioApps).TotalSeconds / $pesoFeito
                    if ($t -lt 0.25) { $t = 0.25 }
                    if ($t -gt 4.0)  { $t = 4.0 }
                    $script:taxa = $t
                }
            }
            if ($script:inicioApps -eq $null) { $script:inicioApps = Get-Date }
            $script:inicioApp = Get-Date
            $script:ultIndice = $indice
            $uiNome.Text = $Programas[$indice].Nome
            $uiSub.Text  = "programa $($indice + 1) de $($Programas.Count)"
            Mostrar-Icone $indice
            Atualizar-Lista $indice
        }

        # Progresso por PESO, com INTERPOLACAO dentro do programa atual.
        #
        # A versao anterior somava so o peso dos programas ja CONCLUIDOS. O programa em
        # andamento contribuia zero, entao a barra ficava parada durante todo ele — e
        # numa ISO com um unico programa (Office) ela ficava em 0% do inicio ao fim, e
        # a estimativa nunca saia de "calculando o tempo restante". Era exatamente o
        # que se via na maquina: 0%, "calculando", e de repente "Tudo pronto".
        #
        # O arquivo de status so diz QUAL programa comecou, nunca o quanto dele ja foi.
        # Entao o avanco dentro do programa e estimado pelo tempo: o peso ja esta em
        # segundos, e o teto de 97% impede a barra de chegar ao fim antes do programa.
        $feito = 0.0
        for ($i = 0; $i -lt $indice; $i++) { $feito += [double]$Programas[$i].Peso }

        $pesoAtual = [double]$Programas[$indice].Peso
        $parcial = 0.0
        if ($script:inicioApp -ne $null -and $pesoAtual -gt 0) {
            $dentro = ((Get-Date) - $script:inicioApp).TotalSeconds
            $parcial = $dentro / ($pesoAtual * $script:taxa)
            if ($parcial -gt 0.97) { $parcial = 0.97 }
            if ($parcial -lt 0) { $parcial = 0 }
        }

        $fracao = ($feito + $parcial * $pesoAtual) / $PesoTotal
        if ($fracao -lt 0) { $fracao = 0 }
        if ($fracao -gt 0.99) { $fracao = 0.99 }

        $uiBarra.Width = [Math]::Round(760 * $fracao)
        $uiPct.Text = "$([Math]::Round($fracao * 100))%"

        # Quantos ainda faltam. Sem tempo estimado: ver a explicacao no XAML.
        $faltam = $Programas.Count - ($indice + 1)
        $uiRest.Text = if ($faltam -gt 1) { "faltam $faltam programas" }
                       elseif ($faltam -eq 1) { 'falta 1 programa' }
                       else { 'ultimo programa' }
    } catch {
        # Status ilegivel num tique (o install.cmd pode estar reescrevendo o arquivo):
        # ignora e tenta no proximo. Nunca derruba a tela — mas REGISTRA, uma vez so,
        # porque um erro que se repete a cada 700 ms enche o log e esconde o resto.
        if (-not $script:erroTique) {
            $script:erroTique = $true
            Reg "erro no tique: $($_.Exception.Message)"
        }
    }
})
$script:erroTique = $false
$script:leuAlgum = $false

# ---------------------------------------------------------------- foco
# O Windows abre o menu Iniciar sozinho no primeiro logon depois de formatar, e
# isso rouba a tela cheia. Nao ha chave suportada para "nao abrir o Iniciar", mas
# a janela pode RETOMAR o foco: a cada 2 s, se nao estiver ativa, ela se reativa
# e o Iniciar recolhe.
#
# Seguro porque durante o provisionamento nao ha nada para o operador clicar — e a
# retomada para no momento em que o status vira "fim", para nao brigar com a
# maquina depois que o trabalho acabou.
$foco = New-Object System.Windows.Threading.DispatcherTimer
$foco.Interval = [TimeSpan]::FromSeconds(2)
$foco.Add_Tick({
    try {
        if ($script:terminou) { $foco.Stop(); return }
        if (-not $win.IsActive) { [void]$win.Activate() }
    } catch { }
})
$script:terminou = $false

# ---------------------------------------------------------------- troca de pagina
# Uma janela, duas paginas. A de progresso so comeca a contar tempo quando aparece:
# se comecasse junto com a tela, o tempo que a pessoa levou escolhendo a unidade
# entraria na conta e a estimativa nasceria mentindo.
function Ir-ParaProgresso {
    # O RELOGIO ARRANCA PRIMEIRO, e sozinho no seu try.
    #
    # Antes ele vinha DEPOIS de Atualizar-Lista, tudo no mesmo try: qualquer tropeco na
    # montagem da lista de etapas abortava o bloco e o $timer.Start() nunca rodava. O
    # sintoma era cruel porque a Visibility ja tinha sido trocada — a pagina de progresso
    # aparecia bonita e ficava CONGELADA em "Preparando o Windows" e 0% enquanto o Office
    # instalava atras, sem nada mudar nunca. Enfeite nao pode impedir o mecanismo.
    try { $timer.Start() } catch { Reg "FALHA AO INICIAR O RELOGIO: $($_.Exception.Message)" }
    try { if ($uiPagU) { $uiPagU.Visibility = 'Collapsed' } } catch { Reg "esconder pagina 1: $($_.Exception.Message)" }
    try { $uiPagP.Visibility = 'Visible' } catch { Reg "mostrar pagina 2: $($_.Exception.Message)" }
    try { Atualizar-Lista 0 } catch { Reg "lista de etapas: $($_.Exception.Message)" }
    try { $foco.Start() } catch { Reg "retomada de foco: $($_.Exception.Message)" }
    Reg "pagina de progresso ativa (relogio=$($timer.IsEnabled))"
}

if ($ComUnidade) {
    # ---------------------------------------------------------------- pagina 1
    $uiUnid.ItemsSource = $Unidades
    $script:escolhido = $null

    function Escolher([string]$novo) {
        if ($script:escolhido) { return }   # clique duplo nao renomeia duas vezes
        $script:escolhido = $novo
        Reg "unidade escolhida: $novo"
        Reg (Renomear $novo)
        try {
            # O install.cmd esta ESPERANDO este arquivo. Ele e gravado depois do
            # rename, entao quando o provisionamento continua a maquina ja tem o nome.
            Set-Content -LiteralPath $UnitFile -Value $novo -Encoding ASCII
        } catch { Reg "falha ao gravar $UnitFile : $($_.Exception.Message)" }
        Ir-ParaProgresso
    }

    $win.AddHandler(
        [System.Windows.Controls.Button]::ClickEvent,
        [System.Windows.RoutedEventHandler]{
            param($sender, $e)
            $src = $e.OriginalSource
            if ($src -is [System.Windows.Controls.Button] -and $src.Tag) {
                Escolher (Get-Hostname $src.Tag)
            }
        })

    $uiBtn.Add_Click({
        if (-not [string]::IsNullOrWhiteSpace($uiTxt.Text)) {
            $n = ($uiTxt.Text -replace '[^A-Za-z0-9\-]', '')
            if ($n.Length -gt 15) { $n = $n.Substring(0,15) }
            $n = $n.Trim('-')
            if ($n) { Escolher $n.ToUpper() }
        }
    })

    # Sem confirmacao em caixa de dialogo, de proposito: a caixa e uma JANELA MODAL, e
    # o temporizador que retoma o foco brigaria com ela exatamente como as duas telas
    # brigavam antes. O clique ja e a confirmacao, e o nome escolhido fica visivel na
    # pagina seguinte.
    Reg 'pagina de unidade ativa; aguardando escolha'
} else {
    Ir-ParaProgresso
}

$win.Add_Loaded({ try { [void]$win.Activate() } catch { } })
if (-not $ReiniciaNoFim) {
    $win.Add_KeyDown({ if ($_.Key -eq 'Escape') { try { $win.Close() } catch { } } })
}

try { [void]$win.ShowDialog() } catch { Reg "ShowDialog falhou: $($_.Exception.Message)" }
Reg 'tela encerrada'
""";
}
