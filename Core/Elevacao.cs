using System.Diagnostics;
using System.IO;

namespace IsoForge.Core;

public enum ResultadoElevacao
{
    /// <summary>A instância elevada começou a subir. Quem chamou deve encerrar esta.</summary>
    Iniciou,
    /// <summary>A pessoa clicou "Não" no UAC. É escolha, não erro.</summary>
    UsuarioRecusou,
    /// <summary>Não consegui descobrir o caminho do próprio executável.</summary>
    SemCaminho,
    Falhou,
}

/// <summary>
/// Reabre o próprio IsoForge com permissão de administrador.
///
/// Não existe "elevar em processo": o token de privilégio é fixado quando o processo nasce.
/// A única forma é lançar outro processo pedindo elevação e encerrar este.
///
/// O QUE NUNCA ATRAVESSA NA LINHA DE COMANDO: configuração, senha, número de disco. A linha
/// de comando de todo processo vai para a telemetria do antivírus e para o registro de
/// eventos, de onde não se apaga. A configuração viaja pelo arquivo cifrado que já existe
/// (<see cref="SettingsStore"/>), que o mesmo usuário elevado abre normalmente.
///
/// E o argumento que atravessa é uma DICA, nunca uma autorização. A instância elevada
/// sempre reapresenta a tela de escolha do pendrive, com a confirmação de "apaga tudo"
/// desmarcada. Um switch do tipo "--gravar-disco 2" transformaria o IsoForge numa arma:
/// qualquer processo comum poderia chamá-lo com "runas" e argumentos próprios, e a pessoa
/// veria o aviso do Windows com o nome de um programa que conhece e aprovaria.
/// </summary>
public static class Elevacao
{
    public static (ResultadoElevacao resultado, string? erro) Relancar(string argumentos)
    {
        // Publicado como arquivo único, os caminhos habituais mentem:
        //   Assembly.Location        -> string VAZIA
        //   AppContext.BaseDirectory -> a pasta de extração, não o executável
        //   GetCommandLineArgs()[0]  -> vem de quem criou o processo; nunca eleve isso
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return (ResultadoElevacao.SemCaminho, null);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = argumentos,
                // UseShellExecute é obrigatório: no .NET moderno o padrão é false, e sem ele
                // o Verb vira enfeite silencioso — o processo nasce sem elevação nenhuma.
                UseShellExecute = true,
                Verb = "runas",
                // WorkingDirectory não vai de propósito: com "runas" a partir de um processo
                // comum o Windows o ignora e o filho começa em System32. Todo caminho nos
                // argumentos precisa ser absoluto.
            });
            return (ResultadoElevacao.Iniciou, null);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (ResultadoElevacao.UsuarioRecusou, null);
        }
        catch (Exception ex)
        {
            return (ResultadoElevacao.Falhou, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Um caminho entre aspas para a linha de comando. As barras finais são duplicadas
    /// porque uma barra invertida imediatamente antes da aspa de fechamento escaparia a
    /// aspa e embaralharia o resto da linha.
    /// </summary>
    public static string Citar(string caminho)
    {
        var c = caminho ?? "";
        var barras = 0;
        for (var i = c.Length - 1; i >= 0 && c[i] == '\\'; i--) barras++;
        return "\"" + c + new string('\\', barras) + "\"";
    }

    /// <summary>
    /// O caminho é aceitável como dica de ISO? Absoluto, com extensão .iso, e existindo.
    /// Vale para o argumento que a instância elevada recebe — que é dado de fora e,
    /// portanto, suspeito até prova em contrário.
    /// </summary>
    public static bool DicaDeIsoValida(string? caminho) =>
        !string.IsNullOrWhiteSpace(caminho)
        && Path.IsPathFullyQualified(caminho)
        && caminho.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
        && File.Exists(caminho);
}
