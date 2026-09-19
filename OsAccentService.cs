using System.Windows;
using System.Windows.Media;
using IsoForge.Models;

namespace IsoForge;

/// <summary>
/// Leva a cor de marca do sistema escolhido para o Shell inteiro.
///
/// Por que existe: <c>OsInfo.Accent</c> alimentava só o tabuleiro de escolha;
/// passada a escolha, o app voltava a ser azul para as onze marcas. Aqui as
/// entradas <c>OsAccent*</c> do dicionário são trocadas em runtime — o mesmo
/// mecanismo do <see cref="ThemeService"/>, e pelo mesmo motivo (pincel de
/// dicionário nasce congelado, então não se muda a cor: muda-se a ENTRADA, e quem
/// consome por <c>{DynamicResource}</c> re-resolve na hora).
///
/// ORDEM IMPORTA: o <see cref="ThemeService"/> NÃO escreve nenhuma chave
/// <c>OsAccent*</c>, justamente para não desfazer a marca ao alternar o tema. Quem
/// troca o tema tem de chamar os dois, nesta ordem:
///     ThemeService.Apply(escuro);
///     OsAccentService.Apply(_config.Os, escuro);
///
/// Legibilidade: cada derivado tem um contrato de contraste MEDIDO, não uma
/// suposição — ver docs/design-abas.md, seção 3.6.
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
        // Texto/ícone: 5,5:1 contra o cartão do tema. A margem de um ponto acima do
        // mínimo é porque este mesmo acento é escrito POR CIMA do véu suave (chip de
        // aplicativo, cabeçalho em hover), e o véu tira ~0,4 do contraste.
        var texto = Ajustar(marca, fundo, 5.5, clarear: escuro);
        // Véu: alfa sobre a superfície do tema.
        var suave = Marca.Alfa(texto, escuro ? (byte)0x38 : (byte)0x24);
        // A barra lateral SEGUE o tema desde que deixou de ser escura nos dois
        // (era um bloco preto no tema claro). Então a variante legível dela é
        // escolhida pelo tema, como qualquer outra superfície.
        var naBarra = Marca.Legivel(marca, escuro);

        res["OsAccentColor"] = cheia;
        res["OsAccent"] = Congelado(new SolidColorBrush(cheia));
        res["OsAccentText"] = Congelado(new SolidColorBrush(texto));
        res["OsAccentSoft"] = Congelado(new SolidColorBrush(suave));
        // A ponta clara do gradiente TAMBEM passa por Tinta. Sem isso o contrato
        // valia so no comeco: medido no botao principal, o branco caia de 4,7:1 para
        // 3,6:1 (Fedora) e 3,3:1 (Ubuntu) ao longo do proprio botao. Quando a marca ja
        // e clara, Tinta devolve quase a mesma cor e o gradiente fica discreto — o que
        // e o comportamento certo: legibilidade antes de brilho.
        res["OsAccentGradient"] = Congelado(new LinearGradientBrush(
            new GradientStopCollection { new(cheia, 0), new(Marca.Tinta(Marca.Escalar(cheia, 1.22)), 1) },
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

        // Os tokens da barra passam a ser da marca. Marca.Legivel garante a
        // luminância mínima contra o gradiente do tema, por construção — no escuro
        // clareando, no claro escurecendo. O alfa do item ativo é maior no claro
        // porque um véu claro sobre fundo claro rende menos que o inverso.
        res["SidebarAccent"] = Congelado(new SolidColorBrush(naBarra));
        res["SidebarActiveBg"] = Congelado(new SolidColorBrush(
            Marca.Alfa(naBarra, escuro ? (byte)0x2E : (byte)0x38)));
    }

    static Brush Congelado(Brush b) { b.Freeze(); return b; }

    /// <summary>
    /// Escurece (ou clareia) a marca até atingir a razão de contraste pedida contra
    /// o fundo indicado. Escalar os três canais preserva o MATIZ; misturar com
    /// branco/preto dessatura, então só entra quando escalar não chega lá — é o caso
    /// das marcas escuras e acinzentadas (AlmaLinux #0F4266 no tema escuro).
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
