Add-Type -AssemblyName "System.Windows.Forms"

# Ocultar a janela do PowerShell
$psWindow = Get-Process -Id $PID
$psWindow.MainWindowHandle | ForEach-Object {
    $sig = '[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);'
    $type = Add-Type -MemberDefinition $sig -Name "Win32ShowWindow" -Namespace Win32Functions -PassThru
    $type::ShowWindow($_, 0) # 0 = Ocultar janela
}

# Definir as localidades disponíveis (exemplos genéricos — ajuste às suas unidades)
$localidades = @{
    "1" = @{ Prefix = "MTZ"; Nome = "Matriz" }
    "2" = @{ Prefix = "FIL"; Nome = "Filial" }
}

# Variável global para controlar o encerramento da aplicação
$script:encerrarAplicacao = $false

# Função para exibir a tela de seleção de hostname
function Show-SelectionForm {
    $form = New-Object System.Windows.Forms.Form
    $form.Text = "Selecione a Unidade"
    $form.Size = New-Object System.Drawing.Size(300, 350)
    $form.StartPosition = "CenterScreen"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false

    $script:escolha = $null

    $label = New-Object System.Windows.Forms.Label
    $label.Text = "Escolha uma unidade:"
    $label.AutoSize = $true
    $label.Top = 10
    $label.Left = 20
    $form.Controls.Add($label)

    $buttonTop = 40
    foreach ($key in $localidades.Keys) {
        $button = New-Object System.Windows.Forms.Button
        $button.Text = "$($localidades[$key].Nome)"
        $button.Width = 250
        $button.Height = 30
        $button.Top = $buttonTop
        $button.Left = 20
        $button.Tag = $key
        $button.Add_Click({
            $script:escolha = $this.Tag
            $form.DialogResult = [System.Windows.Forms.DialogResult]::OK
            $form.Close()
        })
        $form.Controls.Add($button)
        $buttonTop += 40
    }

    $customLabel = New-Object System.Windows.Forms.Label
    $customLabel.Text = "Ou digite um hostname personalizado:"
    $customLabel.AutoSize = $true
    $customLabel.Top = $buttonTop + 10
    $customLabel.Left = 20
    $form.Controls.Add($customLabel)

    $textBox = New-Object System.Windows.Forms.TextBox
    $textBox.Width = 250
    $textBox.Top = $buttonTop + 30
    $textBox.Left = 20
    $form.Controls.Add($textBox)

    $customButton = New-Object System.Windows.Forms.Button
    $customButton.Text = "Usar Hostname Personalizado"
    $customButton.Width = 250
    $customButton.Height = 30
    $customButton.Top = $buttonTop + 60
    $customButton.Left = 20
    $customButton.Add_Click({
        if (-not [string]::IsNullOrWhiteSpace($textBox.Text)) {
            $script:escolha = "custom"
            $script:customHostname = $textBox.Text
            $form.DialogResult = [System.Windows.Forms.DialogResult]::OK
            $form.Close()
        } else {
            [System.Windows.Forms.MessageBox]::Show(
                "Por favor, digite um hostname válido.",
                "Erro",
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Error
            )
        }
    })
    $form.Controls.Add($customButton)

    $form.Add_FormClosing({
        if ($_.CloseReason -eq [System.Windows.Forms.CloseReason]::UserClosing -and $form.DialogResult -ne [System.Windows.Forms.DialogResult]::OK) {
            $script:encerrarAplicacao = $true
        }
    })

    $form.ShowDialog()
}

# Loop principal para permitir voltar à tela de seleção
do {
    Show-SelectionForm

    if ($script:encerrarAplicacao) { Exit }

    if (-not $localidades.ContainsKey($script:escolha) -and $script:escolha -ne "custom") { Exit }

    if ($script:escolha -ne "custom") {
        $serialNumber = (Get-WmiObject Win32_BIOS).SerialNumber
        $prefix = $localidades[$script:escolha].Prefix
        $novoHostname = "$prefix$serialNumber"
    } else {
        $novoHostname = $script:customHostname
    }

    $confirmationForm = New-Object System.Windows.Forms.Form
    $confirmationForm.Text = "Confirmar Hostname"
    $confirmationForm.Size = New-Object System.Drawing.Size(400, 200)
    $confirmationForm.StartPosition = "CenterScreen"
    $confirmationForm.FormBorderStyle = "FixedDialog"
    $confirmationForm.MaximizeBox = $false

    $hostnameLabel = New-Object System.Windows.Forms.Label
    $hostnameLabel.Text = "O novo hostname sera:`n$novoHostname"
    $hostnameLabel.AutoSize = $false
    $hostnameLabel.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
    $hostnameLabel.Dock = [System.Windows.Forms.DockStyle]::Top
    $hostnameLabel.Height = 60
    $hostnameLabel.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
    $confirmationForm.Controls.Add($hostnameLabel)

    $confirmButton = New-Object System.Windows.Forms.Button
    $confirmButton.Text = "Confirmar"
    $confirmButton.Width = 100
    $confirmButton.Height = 30
    $confirmButton.Top = 100
    $confirmButton.Left = 70
    $confirmButton.DialogResult = [System.Windows.Forms.DialogResult]::Yes
    $confirmationForm.Controls.Add($confirmButton)

    $cancelButton = New-Object System.Windows.Forms.Button
    $cancelButton.Text = "Cancelar"
    $cancelButton.Width = 100
    $cancelButton.Height = 30
    $cancelButton.Top = 100
    $cancelButton.Left = 210
    $cancelButton.DialogResult = [System.Windows.Forms.DialogResult]::No
    $confirmationForm.Controls.Add($cancelButton)

    $result = $confirmationForm.ShowDialog()

    if ($result -eq [System.Windows.Forms.DialogResult]::No) {
        continue
    }

    Rename-Computer -NewName $novoHostname -Force

    $restartDialog = [System.Windows.Forms.MessageBox]::Show(
        "O computador precisa ser reiniciado para aplicar as alteracoes.`nDeseja reiniciar agora?",
        "Confirmação de reinício",
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Question
    )

    if ($restartDialog -eq [System.Windows.Forms.DialogResult]::Yes) {
        Restart-Computer -Force
    }
} while ($result -eq [System.Windows.Forms.DialogResult]::No)
