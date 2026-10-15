using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using IsoForge.Models;

namespace IsoForge.Core;

/// <summary>
/// Extrai o ícone de cada instalador e grava como PNG dentro da ISO, para a tela de
/// progresso do primeiro logon mostrar o programa que está sendo instalado.
///
/// Feito no momento da GERAÇÃO, não na máquina de destino: aqui o instalador está no
/// disco, e lá seria mais um passo para dar errado num Windows recém-instalado.
///
/// Usa <c>PrivateExtractIcons</c> + as APIs de imagem do PRÓPRIO WPF, sem
/// System.Drawing: aquele caminho exigiria a referência System.Drawing.Common, e
/// carregar uma dependência inteira para gerar uns PNGs não se paga. De quebra,
/// PrivateExtractIcons aceita o TAMANHO desejado e devolve a melhor variante
/// embutida — pedindo 256 px vem o ícone grande quando o instalador tem um, em vez
/// dos 32 px que o ExtractAssociatedIcon entrega.
///
/// O nome do arquivo é o ÍNDICE do app (1.png, 2.png...), não o nome do programa:
/// nome de app vira caminho inválido com facilidade ("Office 365 (ODT)", acentos,
/// parênteses) e o índice é o que a tela já tem em mãos.
/// </summary>
public static class AppIconExtractor
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int PrivateExtractIcons(string path, int index, int cx, int cy,
                                          [Out] nint[] icons, [Out] int[] ids,
                                          int count, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(nint handle);

    /// <summary>
    /// Grava os ícones em <paramref name="iconDir"/>. Devolve quantos saíram. Falha
    /// de um ícone não interrompe nada — a tela cai no glifo de reserva.
    /// </summary>
    public static int Extract(BuildConfig cfg, string iconDir)
    {
        var apps = cfg.Apps.Where(a => !string.IsNullOrWhiteSpace(a.InstallerPath)).ToList();
        if (apps.Count == 0) return 0;

        Directory.CreateDirectory(iconDir);
        var gravados = 0;

        for (var i = 0; i < apps.Count; i++)
        {
            try
            {
                if (Salvar(apps[i].InstallerPath, Path.Combine(iconDir, $"{i + 1}.png")))
                    gravados++;
            }
            catch
            {
                // Instalador sem ícone, bloqueado ou corrompido: segue em frente.
            }
        }

        return gravados;
    }

    static bool Salvar(string instalador, string destino)
    {
        if (!File.Exists(instalador)) return false;

        // 256 primeiro; 64 como reserva. Um .msi normalmente só tem o ícone genérico
        // do Windows Installer, o que ainda é melhor que nada.
        foreach (var tamanho in new[] { 256, 64 })
        {
            var handles = new nint[1];
            var ids = new int[1];
            if (PrivateExtractIcons(instalador, 0, tamanho, tamanho, handles, ids, 1, 0) <= 0)
                continue;
            if (handles[0] == 0) continue;

            try
            {
                var fonte = Imaging.CreateBitmapSourceFromHIcon(
                    handles[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(fonte));
                using var fs = File.Create(destino);
                enc.Save(fs);
                return true;
            }
            finally
            {
                // Handle de ícone é recurso de GDI: sem DestroyIcon, gerar uma ISO com
                // dez apps vaza dez handles por execução.
                DestroyIcon(handles[0]);
            }
        }

        return false;
    }
}
