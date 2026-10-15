using System.IO;
using System.Text;
using System.Text.Json;

namespace IsoForge.Core;

/// <summary>
/// O que a instância que pediu elevação deixa para a que nasceu elevada, para a gravação
/// continuar de onde parou em vez de pedir tudo de novo.
///
/// POR QUE NÃO VAI NA LINHA DE COMANDO. Um switch do tipo <c>--gravar-disco 2</c> faria do
/// IsoForge uma arma que precisa de um único UAC aprovado: qualquer processo comum poderia
/// chamá-lo com "runas" e argumentos próprios, e a pessoa veria o aviso do Windows com o
/// nome de um programa que conhece. Além disso, linha de comando vai para a telemetria do
/// antivírus e para o registro de eventos, de onde não se apaga.
///
/// E POR QUE ISTO NÃO REABRE O MESMO BURACO. O bastão é um SELETOR, nunca uma autorização:
/// ele diz "era este disco", e a instância elevada vai perguntar ao Windows de novo, do
/// zero, passando por todas as travas — só grava num disco que a consulta DELA devolveu
/// como gravável. O pior que um bastão forjado consegue é apontar para um pendrive que já
/// passaria em todos os testes de qualquer jeito; o disco do sistema continua impossível.
///
/// Ainda assim ele é: cifrado com o mesmo cofre da configuração (outra conta do Windows não
/// lê), de uso único (apagado na leitura) e de vida curta (dois minutos — o tempo de
/// responder ao aviso do Windows).
/// </summary>
public sealed record Bastao(
    int Disco,
    string Modelo,
    long Bytes,
    string? IsoPronta,
    DateTime Criado)
{
    static string Caminho => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IsoForge", "bastao.bin");

    /// <summary>Quanto tempo o bastão vale. É o tempo de responder ao aviso do Windows.</summary>
    public static readonly TimeSpan Validade = TimeSpan.FromMinutes(2);

    public static void Guardar(Bastao b)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(b);
            File.WriteAllBytes(Caminho, Cofre.Cifrar(json));
            Trancar(Caminho);
        }
        catch { /* sem bastão a instância elevada só pergunta de novo: ruim, não fatal */ }
    }

    /// <summary>
    /// Lê e APAGA o bastão. Apaga antes de validar, de propósito: um bastão que não serve
    /// também não pode ficar para trás esperando a próxima abertura do programa.
    /// </summary>
    public static Bastao? Consumir()
    {
        try
        {
            if (!File.Exists(Caminho)) return null;

            var bruto = File.ReadAllBytes(Caminho);
            try { File.Delete(Caminho); } catch { }

            var b = JsonSerializer.Deserialize<Bastao>(Cofre.Decifrar(bruto));
            if (b == null) return null;

            var idade = DateTime.Now - b.Criado;
            if (idade < TimeSpan.Zero || idade > Validade) return null;

            return b;
        }
        catch
        {
            // Cifrado por outra conta do Windows, corrompido, de outra versão: em todos os
            // casos a resposta é a mesma — não confio, pergunto de novo.
            try { File.Delete(Caminho); } catch { }
            return null;
        }
    }

    public static void Descartar()
    {
        try { if (File.Exists(Caminho)) File.Delete(Caminho); } catch { }
    }

    static void Trancar(string caminho)
    {
        if (OperatingSystem.IsWindows()) return;   // no Windows quem protege é a DPAPI
        try { File.SetUnixFileMode(caminho, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { }
    }
}
