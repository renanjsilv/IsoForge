using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using IsoForge.Models;

namespace IsoForge.Core;

/// <summary>
/// Gera a imagem golden de forma totalmente automática:
/// 1) monta uma ISO de referência (disco automático + auditoria + golden.cmd);
/// 2) cria uma VM Hyper-V headless, que instala tudo e faz sysprep sozinha;
/// 3) captura o install.wim do VHDX (DISM, no host);
/// 4) remonta a ISO final usando esse WIM (apps já pré-instalados).
/// Requer Hyper-V habilitado e execução como Administrador.
/// </summary>
public class GoldenAutoBuilder
{
    readonly IProgress<string> _log;
    public GoldenAutoBuilder(IProgress<string> log) => _log = log;

    void Log(string m) => _log.Report(m);

    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public async Task<bool> HyperVAvailableAsync(CancellationToken ct)
    {
        // Get-Command New-VM existe apenas quando o módulo Hyper-V está presente.
        var (exit, _) = await RunPowerShellAsync("if (Get-Command New-VM -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }", ct, stream: false);
        return exit == 0;
    }

    /// <summary>0 = vmms rodando; 1 = existe mas parado; 2 = não existe.</summary>
    public async Task<int> VmmsStateAsync(CancellationToken ct)
    {
        var (exit, _) = await RunPowerShellAsync(
            "$s = Get-Service vmms -ErrorAction SilentlyContinue; if (-not $s) { exit 2 } elseif ($s.Status -ne 'Running') { exit 1 } else { exit 0 }",
            ct, stream: false);
        return exit;
    }

    public async Task BuildAsync(BuildConfig cfg, CancellationToken ct)
    {
        if (!IsAdministrator())
            throw new InvalidOperationException("A imagem golden automática precisa que o IsoForge seja executado como Administrador (Hyper-V, Mount-VHD e DISM exigem elevação).");
        if (!await HyperVAvailableAsync(ct))
            throw new InvalidOperationException(
                "Hyper-V não está disponível. Habilite uma vez (PowerShell como Admin, reinicia o PC):\n" +
                "Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -All");

        var vmms = await VmmsStateAsync(ct);
        if (vmms == 2)
            throw new InvalidOperationException(
                "O Hyper-V está listado, mas o serviço de gerenciamento (vmms) não existe. Confirme que a plataforma Hyper-V foi habilitada e REINICIE o Windows.");
        if (vmms == 1)
            throw new InvalidOperationException(
                "O serviço do Hyper-V (vmms) não está rodando — isso normalmente acontece quando o Hyper-V foi habilitado agora e o Windows AINDA NÃO FOI REINICIADO.\n\n" +
                "Reinicie o computador e tente de novo. (Se persistir após reiniciar, verifique em serviços.msc se 'Hyper-V Virtual Machine Management' está iniciado.)");

        var work = Path.Combine(Path.GetTempPath(), "IsoForge", $"golden_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(work);
        var refIso = Path.Combine(work, "referencia.iso");
        var vhdx = Path.Combine(work, "golden.vhdx");
        var wim = Path.Combine(work, "install.wim");

        try
        {
            if (string.IsNullOrWhiteSpace(cfg.ProductKey))
                Log("Aviso: nenhuma edição/chave definida (aba Sistema e usuário). Se a ISO tiver várias edições, o Setup do Windows pode parar perguntando a edição — nesse caso, escolha a edição e gere de novo.");

            // 1) ISO de referência: instala tudo em auditoria e faz sysprep.
            Log("==== Etapa 1/3: gerando ISO de referência ====");
            var reference = cfg.Clone();
            reference.GoldenReference = true;
            reference.NoPromptBoot = true;      // boot automático na VM
            reference.UseUnitSelection = false; // referência não seleciona unidade
            reference.UseCapturedWim = false;
            reference.OutputIsoPath = refIso;
            await new IsoPipeline(_log).BuildAsync(reference, ct);

            // 2) VM headless: instala, sysprep e desliga; captura o install.wim.
            Log("");
            Log("==== Etapa 2/3: VM Hyper-V (instalação automática + captura) ====");
            Log("Isso pode levar 30–60 min. A janela do Hyper-V vai abrir para você acompanhar — não interaja; a VM conclui e desliga sozinha.");
            await RunOrchestrationAsync(refIso, vhdx, wim, cfg, ct);

            if (!File.Exists(wim))
                throw new InvalidOperationException("A captura terminou mas o install.wim não foi encontrado.");
            Log($"install.wim capturado: {new FileInfo(wim).Length / 1024.0 / 1024.0 / 1024.0:F2} GB");

            // 3) ISO final: usa o WIM capturado; apps/office já estão dentro dele.
            Log("");
            Log("==== Etapa 3/3: remontando a ISO final (golden) ====");
            var final = cfg.Clone();
            final.UseCapturedWim = true;
            final.CapturedWimPath = wim;
            final.GoldenReference = false;
            final.Apps.Clear();                       // já instalados no WIM
            final.OfficeOffline = false;
            final.WallpaperPath = "";                 // já aplicado no WIM
            final.LockScreenPath = "";                // já aplicado no WIM
            final.VpnTunnels.Clear();                 // já configurado no WIM
            final.FortiClientRegImportPath = "";
            final.OutputIsoPath = cfg.OutputIsoPath;
            await new IsoPipeline(_log).BuildAsync(final, ct);

            Log("");
            Log($"✔ Imagem golden pronta: {cfg.OutputIsoPath}");
            Log("O Windows instalado a partir dela já vem com tudo, sem instalar nada no 1º boot.");

            // Só limpa a pasta de trabalho no SUCESSO (a VM/WIM já foram usados).
            try { if (Directory.Exists(work)) Directory.Delete(work, true); }
            catch (Exception ex) { Log($"Aviso: não foi possível limpar {work}: {ex.Message}"); }
        }
        catch
        {
            // Em caso de falha, PRESERVA os arquivos (a VM já instalada não é apagada).
            Log($"⚠ Falha na geração. Os arquivos foram MANTIDOS em: {work}");
            Log("   (inclui o disco da VM já instalada, se chegou a instalar). Apague essa pasta manualmente para liberar espaço quando não precisar mais.");
            throw;
        }
    }

    async Task RunOrchestrationAsync(string refIso, string vhdx, string wim, BuildConfig cfg, CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetDirectoryName(vhdx)!, "orchestrate.ps1");
        // UTF-8 COM BOM: o Windows PowerShell 5.1 lê .ps1 sem BOM como ANSI e quebra acentos.
        File.WriteAllText(scriptPath, GoldenImageScripts.Orchestrate, new UTF8Encoding(true));

        var args =
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
            $"-RefIso \"{refIso}\" -Vhdx \"{vhdx}\" -WimOut \"{wim}\" " +
            $"-MemoryGB {cfg.GoldenVmMemoryGB} -DiskGB {cfg.GoldenVmDiskGB} -TimeoutMin {cfg.GoldenTimeoutMinutes}";

        var (exit, _) = await RunPowerShellFileAsync(args, ct);
        if (exit != 0)
            throw new InvalidOperationException($"A orquestração da VM falhou (código {exit}). Veja o log acima.");
    }

    // ------------------------------------------------------------------
    async Task<(int exit, string output)> RunPowerShellAsync(string command, CancellationToken ct, bool stream)
    {
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return await RunAsync(psi, ct, stream);
    }

    async Task<(int exit, string output)> RunPowerShellFileAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return await RunAsync(psi, ct, stream: true);
    }

    async Task<(int exit, string output)> RunAsync(ProcessStartInfo psi, CancellationToken ct, bool stream)
    {
        var sb = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { sb.AppendLine(e.Data); if (stream) Log("  " + e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { sb.AppendLine(e.Data); if (stream) Log("  " + e.Data); } };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, sb.ToString());
    }
}
