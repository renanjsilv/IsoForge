using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using IsoForge.Core;

namespace IsoForge.Linux;

/// <summary>Um disco removível visto pelo sistema.</summary>
/// <param name="Caminho">Dispositivo (ex.: /dev/sdb).</param>
/// <param name="Modelo">Modelo informado pelo fabricante.</param>
/// <param name="Bytes">Tamanho em bytes.</param>
/// <param name="Transporte">Barramento (usb, sata...).</param>
/// <param name="Montagens">Pontos de montagem das partições, se houver.</param>
public record DiscoRemovivel(string Caminho, string Modelo, long Bytes, string Transporte, string[] Montagens)
{
    public string Tamanho => Bytes >= 1L << 30
        ? $"{Bytes / 1024.0 / 1024 / 1024:F1} GB"
        : $"{Bytes / 1024.0 / 1024:F0} MB";

    public string Descricao =>
        $"{Caminho} — {(string.IsNullOrWhiteSpace(Modelo) ? "sem modelo" : Modelo)} ({Tamanho})";
}

/// <summary>
/// Lista pendrives e grava uma ISO neles.
///
/// A gravação é um <c>dd</c> da imagem para o dispositivo, que é como as ISOs oficiais das
/// distribuições são feitas para serem gravadas: elas já são híbridas, com a tabela de partições
/// dentro da própria imagem. Não há formatação, nem cópia de arquivo por arquivo — por isso o
/// pendrive inteiro é sobrescrito.
/// </summary>
public static class Pendrive
{
    /// <summary>
    /// Discos que podem receber a gravação. Três exclusões, todas propositais: só o que o
    /// kernel marca como disco (não partição), só o que é removível ou está num barramento
    /// USB, e nunca o disco de onde o sistema está rodando.
    /// </summary>
    public static async Task<IReadOnlyList<DiscoRemovivel>> ListarAsync(CancellationToken ct)
    {
        var lsblk = Plataforma.NoCaminho("lsblk") ?? "/usr/bin/lsblk";

        // MOUNTPOINTS (plural) só existe do util-linux 2.33 em diante. Em distribuição mais
        // antiga o lsblk recusa a coluna inteira e não lista nada; aí vale o MOUNTPOINT antigo.
        var (codigo, saida) = await IsoTools.TryRunCapturedAsync(
            lsblk, "--json --bytes --output NAME,SIZE,MODEL,TRAN,RM,TYPE,MOUNTPOINTS,PATH", ct);
        if (codigo != 0)
            (codigo, saida) = await IsoTools.TryRunCapturedAsync(
                lsblk, "--json --bytes --output NAME,SIZE,MODEL,TRAN,RM,TYPE,MOUNTPOINT,PATH", ct);
        if (codigo != 0)
            throw new InvalidOperationException($"Não consegui listar os discos (lsblk): {saida.Trim()}");

        var doRaiz = DiscoDaRaiz();
        var lista = new List<DiscoRemovivel>();

        using var doc = JsonDocument.Parse(saida);
        if (!doc.RootElement.TryGetProperty("blockdevices", out var discos)) return lista;

        foreach (var d in discos.EnumerateArray())
        {
            if (Texto(d, "type") != "disk") continue;

            var removivel = d.TryGetProperty("rm", out var rm) && rm.ValueKind == JsonValueKind.True;
            var transporte = Texto(d, "tran");
            if (!removivel && transporte != "usb") continue;

            var caminho = Texto(d, "path");
            if (string.IsNullOrEmpty(caminho)) caminho = "/dev/" + Texto(d, "name");
            if (caminho == doRaiz) continue;

            var montagens = Montagens(d).ToArray();
            // Um disco removível que carrega a raiz do sistema existe (quem dá boot por USB):
            // ele não pode aparecer na lista de forma alguma.
            if (montagens.Any(m => m == "/" || m == "/boot" || m == "/boot/efi")) continue;

            lista.Add(new DiscoRemovivel(
                caminho,
                Texto(d, "model"),
                d.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0,
                transporte,
                montagens));
        }

        return lista;
    }

    static string Texto(JsonElement e, string nome) =>
        e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static IEnumerable<string> Montagens(JsonElement disco)
    {
        if (disco.TryGetProperty("mountpoints", out var m) && m.ValueKind == JsonValueKind.Array)
            foreach (var x in m.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) yield return x.GetString() ?? "";

        // Forma antiga: uma string só, em vez da lista.
        if (disco.TryGetProperty("mountpoint", out var mp) && mp.ValueKind == JsonValueKind.String)
            yield return mp.GetString() ?? "";

        if (!disco.TryGetProperty("children", out var filhos) || filhos.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var filho in filhos.EnumerateArray())
            foreach (var x in Montagens(filho))
                yield return x;
    }

    /// <summary>Disco onde a raiz do sistema está — o único que jamais pode ser oferecido.</summary>
    static string DiscoDaRaiz()
    {
        try
        {
            var findmnt = Plataforma.NoCaminho("findmnt");
            if (findmnt == null) return "";
            var psi = new ProcessStartInfo(findmnt, "-n -o SOURCE --target /")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var fonte = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            // /dev/sda2 -> /dev/sda ; /dev/nvme0n1p2 -> /dev/nvme0n1
            var m = System.Text.RegularExpressions.Regex.Match(fonte, @"^(/dev/(?:nvme\d+n\d+|mmcblk\d+|[a-z]+))");
            return m.Success ? m.Groups[1].Value : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// O comando exato que a gravação executa — mostrado antes de rodar e repetido na mensagem
    /// de erro quando não há pkexec, para quem quiser rodar no terminal. Os caminhos saem
    /// citados de verdade: uma aspa simples no nome do arquivo tornaria a linha colada no
    /// terminal outra coisa.
    /// </summary>
    public static string Comando(string iso, string dispositivo) =>
        $"dd if={Citar(iso)} of={Citar(dispositivo)} bs=4M oflag=direct conv=fsync status=progress";

    static string Citar(string valor) => "'" + valor.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Grava a ISO no dispositivo. Apaga tudo que estiver lá.
    ///
    /// A gravação precisa de root; quem eleva é o <c>pkexec</c>, que abre a janela de senha do
    /// próprio ambiente gráfico. Sem pkexec não há elevação silenciosa possível — nesse caso o
    /// método falha dizendo o comando a executar no terminal, em vez de tentar algum atalho.
    /// </summary>
    public static async Task GravarAsync(string iso, string dispositivo, Action<string> log,
        IProgress<int>? percentual, CancellationToken ct)
    {
        if (!File.Exists(iso)) throw new FileNotFoundException("ISO não encontrada.", iso);

        // Reconfere o dispositivo AGORA: entre listar e clicar o usuário pode ter trocado o
        // pendrive de porta, e o /dev/sdb de antes pode ser outro disco agora.
        var atuais = await ListarAsync(ct);
        var alvo = atuais.FirstOrDefault(d => d.Caminho == dispositivo)
            ?? throw new InvalidOperationException(
                $"{dispositivo} não está mais na lista de discos removíveis. Reconecte o pendrive e tente de novo.");
        log($"Gravando em {alvo.Descricao}.");

        var pkexec = Plataforma.NoCaminho("pkexec");
        if (pkexec == null)
            throw new InvalidOperationException(
                "pkexec não encontrado — sem ele não dá para pedir a senha de administrador pela janela.\n\n" +
                "Rode no terminal:\n\n  sudo " + Comando(iso, dispositivo));

        var total = new FileInfo(iso).Length;
        var escrito = 0L;

        var psi = new ProcessStartInfo(pkexec)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in new[]
                 {
                     "dd", $"if={iso}", $"of={dispositivo}",
                     "bs=4M", "oflag=direct", "conv=fsync", "status=progress"
                 })
            psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        // O dd escreve o progresso no stderr, em linhas que começam com o total de bytes.
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            var m = System.Text.RegularExpressions.Regex.Match(e.Data, @"^(\d+) bytes");
            if (m.Success && long.TryParse(m.Groups[1].Value, out var n))
            {
                escrito = n;
                if (total > 0) percentual?.Report((int)Math.Min(100, escrito * 100 / total));
            }
            else log("  " + e.Data.Trim());
        };
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log("  " + e.Data.Trim()); };

        if (!p.Start()) throw new InvalidOperationException("Não foi possível iniciar o pkexec.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);

        if (p.ExitCode == 126 || p.ExitCode == 127)
            throw new InvalidOperationException("A autorização foi negada ou cancelada — nada foi gravado.");
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"O dd terminou com código {p.ExitCode}. Nada garante que o pendrive esteja utilizável.");

        percentual?.Report(100);
        log($"Gravação concluída ({escrito / 1024.0 / 1024 / 1024:F2} GB).");
    }
}
