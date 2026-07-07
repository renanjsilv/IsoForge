using System.IO;
using System.Text;
using IsoForge.Models;

namespace IsoForge.Core;

/// <summary>
/// Gera o SelectUnit.ps1: uma tela WPF em tela cheia (usando o PresentationFramework
/// nativo do Windows) exibida no MODO DE AUDITORIA, antes de qualquer usuário ser
/// criado. O operador escolhe a unidade, o hostname vira PREFIXO + número de série
/// do BIOS, a máquina é renomeada e o Windows reinicia para o OOBE
/// (sysprep /oobe /reboot), onde o usuário local é finalmente criado.
/// </summary>
public static class UnitSelectorGenerator
{
    public const string FileName = "SelectUnit.ps1";

    public static string Generate(BuildConfig cfg)
    {
        // Monta o array PowerShell de unidades a partir da configuração.
        var units = new StringBuilder();
        foreach (var u in cfg.Units)
        {
            if (string.IsNullOrWhiteSpace(u.Name) || string.IsNullOrWhiteSpace(u.Prefix)) continue;
            var name = u.Name.Replace("'", "''");
            var prefix = u.Prefix.Replace("'", "''");
            units.AppendLine($"    [pscustomobject]@{{ Nome = '{name}'; Prefixo = '{prefix}' }},");
        }

        bool entra = cfg.Mode == DeploymentMode.EntraId && cfg.UseUnitSelection;
        bool audit = !entra && cfg.UnitMethod == UnitSelectionMethod.Audit;

        // Final do script depende do cenário:
        // - Entra ID: o usuário é PADRÃO (sem admin) e não pode renomear. A tela só grava a
        //   escolha; a tarefa IsoForge-RenameUnit (SYSTEM) renomeia e reinicia.
        // - Auditoria: renomeia e faz sysprep /oobe /reboot (leva ao OOBE, onde o usuário é criado).
        // - 1º logon (Conta local): só renomeia; o install.cmd reinicia uma vez no fim.
        string ending;
        if (entra)
        {
            ending = """
if ($script:hostChoice) {
    # Modo Entra ID: usuario padrao nao pode renomear. Grava a escolha para a tarefa SYSTEM.
    $dir = Join-Path $env:ProgramData 'IsoForge'
    New-Item -ItemType Directory -Force $dir | Out-Null
    Set-Content -Path (Join-Path $dir 'unit.txt') -Value $script:hostChoice -Encoding ASCII
}
""";
        }
        else if (audit)
        {
            ending = """
if ($script:hostChoice) {
    try {
        Rename-Computer -NewName $script:hostChoice -Force -ErrorAction Stop
        Write-Output "IsoForge: computador renomeado para $script:hostChoice."
    } catch {
        Write-Output "IsoForge: Rename-Computer falhou ($($_.Exception.Message)); tentando via CIM..."
        try { $rc = Invoke-CimMethod -ClassName Win32_ComputerSystem -MethodName Rename -Arguments @{ Name = $script:hostChoice }; Write-Output ("IsoForge: rename CIM ReturnValue=" + $rc.ReturnValue) } catch { Write-Output "IsoForge: rename CIM falhou: $($_.Exception.Message)" }
    }
    Start-Process -FilePath "$env:windir\System32\Sysprep\sysprep.exe" -ArgumentList '/oobe','/reboot','/quiet'
}
""";
        }
        else
        {
            ending = """
if ($script:hostChoice) {
    # Sem auditoria: apenas renomeia. O install.cmd reinicia a maquina no fim, aplicando o nome.
    try {
        Rename-Computer -NewName $script:hostChoice -Force -ErrorAction Stop
        Write-Output "IsoForge: computador renomeado para $script:hostChoice (aplica no reboot final)."
    } catch {
        Write-Output "IsoForge: Rename-Computer falhou ($($_.Exception.Message)); tentando via CIM..."
        try { $rc = Invoke-CimMethod -ClassName Win32_ComputerSystem -MethodName Rename -Arguments @{ Name = $script:hostChoice }; Write-Output ("IsoForge: rename CIM ReturnValue=" + $rc.ReturnValue) } catch { Write-Output "IsoForge: rename CIM falhou: $($_.Exception.Message)" }
    }
}
""";
        }

        var titleComment = entra
            ? "# IsoForge - Selecao de unidade (modo Entra ID, 1o logon do usuario)\n# Usuario padrao: grava a escolha; a tarefa SYSTEM renomeia e reinicia."
            : audit
                ? "# IsoForge - Selecao de unidade (modo de auditoria)\n# Executado como Administrador interno, antes do usuario existir."
                : "# IsoForge - Selecao de unidade (1o logon, sem auditoria)\n# Executado no primeiro logon; renomeia e o install.cmd reinicia no fim.";

        // Mensagem de confirmacao (deixa claro QUANDO reinicia, para nao parecer reboot imediato).
        var confirmMsg = entra
            ? "A maquina sera renomeada e reiniciada agora."
            : audit
                ? "Confirmar e reiniciar?"
                : "Os programas continuam instalando; a maquina reinicia sozinha no final.";

        return $$"""
{{titleComment}}
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$ErrorActionPreference = 'Stop'
try { [Console]::Title = 'IsoForge' } catch {}

$Unidades = @(
{{units}}    $null
) | Where-Object { $_ }

function Get-Hostname([string]$prefixo) {
    $serial = (Get-CimInstance Win32_BIOS).SerialNumber
    if ([string]::IsNullOrWhiteSpace($serial)) { $serial = 'PC' }
    $nome = ($prefixo + $serial)
    # NetBIOS: no maximo 15 caracteres e apenas letras/numeros/hifen
    $nome = ($nome -replace '[^A-Za-z0-9\-]', '')
    if ($nome.Length -gt 15) { $nome = $nome.Substring(0, 15) }
    # NetBIOS nao pode comecar/terminar com hifen (ficava "MTZ7808-4244-")
    $nome = $nome.Trim('-')
    if ([string]::IsNullOrWhiteSpace($nome)) { $nome = 'PC' }
    return $nome.ToUpper()
}

[xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" WindowState="Maximized" Topmost="True"
        ResizeMode="NoResize" Background="#0F172A" FontFamily="Segoe UI Variable, Segoe UI">
  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
      <RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>

    <StackPanel Grid.Row="0" HorizontalAlignment="Center" Margin="0,70,0,20">
      <TextBlock Text="Selecione a unidade" Foreground="White" FontSize="42" FontWeight="SemiBold" HorizontalAlignment="Center"/>
      <TextBlock Text="Isso define o nome deste computador" Foreground="#94A3B8" FontSize="18" HorizontalAlignment="Center" Margin="0,8,0,0"/>
    </StackPanel>

    <ItemsControl x:Name="Lista" Grid.Row="1" HorizontalAlignment="Center" VerticalAlignment="Center" Width="900">
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
</Window>
"@

$reader = New-Object System.Xml.XmlNodeReader $xaml
$window = [Windows.Markup.XamlReader]::Load($reader)
$lista  = $window.FindName('Lista')
$txt    = $window.FindName('TxtCustom')
$btn    = $window.FindName('BtnCustom')
$lista.ItemsSource = $Unidades

$script:hostChoice = $null

function Confirmar([string]$novo) {
    $msg = "O novo nome do computador sera:`n`n$novo`n`n{{confirmMsg}}"
    $r = [System.Windows.MessageBox]::Show($window, $msg, 'Confirmar', 'YesNo', 'Question')
    if ($r -eq 'Yes') { $script:hostChoice = $novo; $window.Close() }
}

foreach ($item in $lista.Items) { } # garante materializacao
$window.AddHandler(
    [System.Windows.Controls.Button]::ClickEvent,
    [System.Windows.RoutedEventHandler]{
        param($sender, $e)
        $src = $e.OriginalSource
        if ($src -is [System.Windows.Controls.Button] -and $src.Tag) {
            Confirmar (Get-Hostname $src.Tag)
        }
    })

$btn.Add_Click({
    if (-not [string]::IsNullOrWhiteSpace($txt.Text)) {
        $n = ($txt.Text -replace '[^A-Za-z0-9\-]', '')
        if ($n.Length -gt 15) { $n = $n.Substring(0,15) }
        Confirmar $n.ToUpper()
    }
})

# Impede fechar sem escolher
$window.Add_Closing({ if (-not $script:hostChoice) { $_.Cancel = $true } })

[void]$window.ShowDialog()

{{ending}}
""";
    }

    public static void WriteTo(BuildConfig cfg, string filePath)
        // UTF-8 COM BOM: garante que o Windows PowerShell leia os acentos corretamente.
        => File.WriteAllText(filePath, Generate(cfg), new UTF8Encoding(true));
}
