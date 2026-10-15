using System.IO;

namespace IsoForge.Core;

/// <summary>
/// A fonte OFFLINE do Office: onde ela está, se está completa, e o que exatamente
/// copiar para dentro da ISO.
///
/// Existe por causa de dois defeitos reais:
///
///  1. <b>Copiávamos a pasta ESCOLHIDA inteira.</b> O usuário aponta a pasta que
///     recebeu o download — na prática costuma ser Downloads —, e o pipeline fazia
///     um CopyDirectory dela para dentro da ISO. Medido num caso real: 12,55 GB e
///     118 arquivos sem relação com o Office indo para a imagem, contra 3,64 GB de
///     payload. Além do tamanho, é vazamento: tudo que estiver na pasta viaja na
///     ISO. Aqui só a subpasta Office vai.
///
///  2. <b>Não validávamos nada.</b> Uma fonte incompleta (download interrompido)
///     era embutida sem aviso; o ODT, no primeiro logon, ia buscar no CDN o que
///     faltava e falhava com "we weren't able to download a required file" — depois
///     de 20 minutos gerando a ISO. Agora a checagem acontece ANTES.
/// </summary>
public static class OfficeSource
{
    /// <summary>Tamanho mínimo plausível de um payload do M365 x64 com um idioma.</summary>
    const double MinimoGb = 2.5;

    /// <summary>
    /// Acha a pasta "Office" do payload dentro do que o usuário apontou. Aceita as
    /// duas formas de apontar: a pasta que CONTÉM Office\Data (o que o /download
    /// produz) ou a própria pasta Office.
    /// </summary>
    public static string? Resolver(string pastaEscolhida)
    {
        if (string.IsNullOrWhiteSpace(pastaEscolhida) || !Directory.Exists(pastaEscolhida))
            return null;

        var comoBaixado = Path.Combine(pastaEscolhida, "Office", "Data");
        if (Directory.Exists(comoBaixado)) return Path.Combine(pastaEscolhida, "Office");

        // Apontou direto para a pasta Office.
        if (Directory.Exists(Path.Combine(pastaEscolhida, "Data"))) return pastaEscolhida;

        return null;
    }

    /// <summary>
    /// A versão da fonte: o nome da subpasta mais NOVA de Office\Data.
    ///
    /// FONTE ÚNICA DE VERDADE. Antes havia duas: o Configuration.xml era fixado por
    /// <c>OfficeConfig.DetectVersion</c> (que ordenava por STRING — e "16.0.9126.2152"
    /// vem depois de "16.0.20326.20132" porque '9' &gt; '2', ou seja, escolhia a versão
    /// MAIS VELHA) e a checagem prévia do install.cmd usava o <c>FirstOrDefault</c>
    /// daqui (ordem do sistema de arquivos, nenhuma). Com duas pastas de versão em Data
    /// — o que um /download retomado deixa — o XML fixava uma versão e o script conferia
    /// outra: a checagem dizia "versão X presente" e o ODT procurava a versão Y, que não
    /// estava lá, e ia ao CDN. Agora os dois chamam ESTE método, que ordena por
    /// <see cref="Version"/> de verdade.
    /// </summary>
    public static string? VersaoMaisNova(string dataDir)
    {
        try
        {
            if (!Directory.Exists(dataDir)) return null;
            return Directory.EnumerateDirectories(dataDir)
                .Select(Path.GetFileName)
                .Where(n => n != null && System.Text.RegularExpressions.Regex
                    .IsMatch(n, @"^\d+\.\d+\.\d+\.\d+$"))
                .Select(n => (nome: n!, ver: Version.Parse(n!)))
                .OrderBy(t => t.ver)
                .Select(t => t.nome)
                .LastOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Quantas subpastas de versão existem em Data (mais de uma = /download retomado).</summary>
    public static int ContarVersoes(string dataDir)
    {
        try
        {
            if (!Directory.Exists(dataDir)) return 0;
            return Directory.EnumerateDirectories(dataDir)
                .Select(Path.GetFileName)
                .Count(n => n != null && System.Text.RegularExpressions.Regex
                    .IsMatch(n, @"^\d+\.\d+\.\d+\.\d+$"));
        }
        catch { return 0; }
    }

    /// <summary>
    /// Os arquivos que o Click-to-Run BUSCA NO CDN quando não os encontra na fonte local,
    /// com o tamanho que eles têm aqui. É a lista que a checagem prévia grava no
    /// install.cmd para conferir byte a byte na máquina de destino.
    ///
    /// Os caminhos são relativos a Office\Data.
    /// </summary>
    public static List<(string rel, long tamanho)> ArquivosCriticos(string pastaEscolhida)
    {
        var lista = new List<(string, long)>();
        var office = Resolver(pastaEscolhida);
        if (office == null) return lista;
        var data = Path.Combine(office, "Data");
        var versao = VersaoMaisNova(data);
        if (versao == null) return lista;

        try
        {
            // Os v*.cab ficam na raiz de Data; o resto do payload na pasta da versão.
            foreach (var f in new DirectoryInfo(data).EnumerateFiles("v*.cab", SearchOption.TopDirectoryOnly))
                lista.Add((f.Name, f.Length));
            foreach (var f in new DirectoryInfo(Path.Combine(data, versao)).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                lista.Add((versao + "\\" + f.Name, f.Length));
        }
        catch { /* melhor esforço: o que der para conferir, confere */ }

        // Teto de segurança: o gate vira linhas de batch, e um script gigante seria pior
        // que o problema. Uma fonte normal tem ~15 arquivos.
        return lista.OrderByDescending(t => t.Item2).Take(40).OrderBy(t => t.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Os idiomas do Configuration.xml que NÃO existem no payload baixado.
    ///
    /// EXISTE POR CAUSA DE UMA FALHA MEDIDA: o XML de instalação exigia uma cultura que o
    /// /download nunca tinha baixado (o &lt;RemoveMSI/&gt; fazia o bootstrapper pedir en-us
    /// num payload só pt-br) e o resultado foi um 1603 em ~70 s, com o log dizendo
    /// "Current culture is unreachable ... Culture: en-us". O RemoveMSI já saiu do XML, mas
    /// a MESMA assimetria acontece se alguém baixar em pt-br e depois trocar o idioma na
    /// tela: download e instalação têm de concordar sobre o conjunto de culturas, e isso
    /// tem de ser conferido AQUI, na máquina que gera, não na casa do cliente.
    ///
    /// O LCID vem do próprio .NET (pt-br → 1046), não de uma tabela nossa. Idioma que o
    /// .NET não reconhece é ignorado: melhor não conferir do que acusar falso.
    /// </summary>
    public static List<string> CulturasFaltando(string pastaEscolhida, IEnumerable<string> idiomas)
    {
        var faltando = new List<string>();
        var office = Resolver(pastaEscolhida);
        if (office == null) return faltando;
        var data = Path.Combine(office, "Data");
        var versao = VersaoMaisNova(data);
        if (versao == null) return faltando;

        try
        {
            var dir = new DirectoryInfo(Path.Combine(data, versao));
            if (!dir.Exists) return faltando;
            var arqs = dir.GetFiles();
            var nomes = arqs.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var arch = arqs.Any(f => f.Name.StartsWith("stream.x86.", StringComparison.OrdinalIgnoreCase)) &&
                       !arqs.Any(f => f.Name.StartsWith("stream.x64.", StringComparison.OrdinalIgnoreCase))
                       ? "32" : "64";

            foreach (var id in idiomas)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                // MatchOS/MatchPreviousMSI sao valores especiais do ODT, nao culturas.
                if (id.StartsWith("Match", StringComparison.OrdinalIgnoreCase)) continue;
                int lcid;
                try { lcid = System.Globalization.CultureInfo.GetCultureInfo(id).LCID; }
                catch { continue; }
                if (lcid <= 0 || lcid == 0x1000) continue;   // cultura sem LCID conhecido
                if (!nomes.Contains($"i{arch}{lcid}.cab") || !nomes.Contains($"s{arch}{lcid}.cab"))
                    faltando.Add($"{id} (LCID {lcid})");
            }
        }
        catch { /* se nao der para listar, nao inventa falha */ }
        return faltando;
    }

    /// <summary>
    /// Diz se a fonte serve para instalar SEM internet. Devolve o problema em texto
    /// quando não serve — a mensagem vai direto para o usuário.
    /// </summary>
    public static (bool ok, string? problema, double gb, string? versao) Validar(string pastaEscolhida)
    {
        var office = Resolver(pastaEscolhida);
        if (office == null)
            return (false, "Não encontrei a pasta Office\\Data aí. Aponte a pasta que recebeu o "
                         + "download (a que contém Office\\Data) ou a própria pasta Office.", 0, null);

        var data = Path.Combine(office, "Data");

        // A versao e o nome da subpasta em Data (ex.: 16.0.20326.20132). E ela que
        // fixamos no Configuration.xml para o ODT nao consultar o CDN.
        var versao = VersaoMaisNova(data);

        if (versao == null)
            return (false, $"A pasta {data} não tem uma subpasta de versão (ex.: 16.0.20326.20132). "
                         + "O download não chegou a começar de verdade.", 0, null);

        var arqs = new DirectoryInfo(data).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        var gb = arqs.Sum(f => f.Length) / 1024.0 / 1024.0 / 1024.0;

        // O stream principal é o que carrega o Office; sem ele não há instalação
        // offline possível, por mais que a pasta pareça povoada.
        var temStream = arqs.Any(f => f.Name.StartsWith("stream.", StringComparison.OrdinalIgnoreCase)
                                      && f.Name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
                                      && f.Length > 1024L * 1024 * 512);
        if (!temStream)
            return (false, $"A fonte em {data} está INCOMPLETA ({gb:F2} GB): não há um "
                         + "stream.*.dat de tamanho plausível. O download foi interrompido — "
                         + "refaça o download do Office antes de gerar a ISO.", gb, versao);

        if (gb < MinimoGb)
            return (false, $"A fonte em {data} tem só {gb:F2} GB — um Office 365 x64 com um idioma "
                         + $"passa de {MinimoGb:F1} GB. O download foi interrompido; refaça antes "
                         + "de gerar a ISO, senão o ODT vai tentar a internet no 1º logon.", gb, versao);

        var faltando = ManifestosFaltando(data, versao);
        if (faltando.Count > 0)
            return (false, $"A fonte em {data} tem os {gb:F2} GB de dados mas está SEM os manifestos/"
                         + $"catálogos: {string.Join(", ", faltando)}.\n\n"
                         + "Esses arquivos são pequenos e é justamente o que o Click-to-Run vai buscar "
                         + "no CDN quando não os acha na fonte local — numa máquina recém-instalada sem "
                         + "rede, é isso que vira \"we weren't able to download a required file\". "
                         + "Refaça o download do Office antes de gerar a ISO.", gb, versao);

        return (true, null, gb, versao);
    }

    /// <summary>
    /// Os arquivos pequenos SEM os quais o ODT vai ao CDN — mesmo com os 3 GB de stream
    /// no lugar.
    ///
    /// MEDIDO, não suposto: rodando <c>setup.exe /download</c> contra uma fonte tida como
    /// completa, o log do Click-to-Run (evento Office.ClickToRun.Transport2, campo
    /// Data.SourcePathNoFilePath) registra GETs para officecdn.microsoft.com de
    /// v64_&lt;versão&gt;.cab, i640.cab, s640.cab, i64&lt;LCID&gt;.cab, s64&lt;LCID&gt;.cab
    /// e de um .cat para cada stream. São os manifestos (descrevem o payload) e os
    /// catálogos de assinatura (validam o stream antes de abri-lo). O "exit 0, 0 MB
    /// baixados" que usávamos como prova de fonte completa só valia porque a máquina que
    /// gera tem internet: o ODT foi na rede calado.
    /// </summary>
    static List<string> ManifestosFaltando(string data, string versao)
    {
        var faltando = new List<string>();
        try
        {
            var dirVersao = new DirectoryInfo(Path.Combine(data, versao));
            if (!dirVersao.Exists) return faltando;
            var arqs = dirVersao.GetFiles();
            var nomes = arqs.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // A arquitetura vem do proprio payload (x64 -> v64/i64/s64; x86 -> v32/i32/s32),
            // para a checagem valer tambem se alguem editar o OfficeClientEdition no XML.
            var arch = arqs.Any(f => f.Name.StartsWith("stream.x86.", StringComparison.OrdinalIgnoreCase)) &&
                       !arqs.Any(f => f.Name.StartsWith("stream.x64.", StringComparison.OrdinalIgnoreCase))
                       ? "32" : "64";

            foreach (var v in new[] { $"v{arch}.cab", $"v{arch}_{versao}.cab" })
                if (!File.Exists(Path.Combine(data, v))) faltando.Add(v);

            foreach (var m in new[] { $"i{arch}0.cab", $"s{arch}0.cab" })
                if (!nomes.Contains(m)) faltando.Add(versao + "\\" + m);

            // Um catalogo de assinatura para CADA stream: e o que o C2R confere antes de
            // abrir os 3 GB. Sem ele o stream local e inutil e o ODT busca o .cat no CDN.
            foreach (var s in arqs.Where(f => f.Name.StartsWith("stream.", StringComparison.OrdinalIgnoreCase)
                                              && f.Name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)))
                if (!nomes.Contains(s.Name + ".cat")) faltando.Add(versao + "\\" + s.Name + ".cat");

            // Manifesto de idioma sempre vem em par i<LCID>/s<LCID>: um sem o outro e
            // download pela metade.
            foreach (var i in arqs.Where(f => System.Text.RegularExpressions.Regex
                         .IsMatch(f.Name, $@"^i{arch}\d+\.cab$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            {
                var par = "s" + i.Name.Substring(1);
                if (!nomes.Contains(par)) faltando.Add(versao + "\\" + par);
            }
        }
        catch { /* se nao der para listar, nao inventa falha */ }
        return faltando.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
