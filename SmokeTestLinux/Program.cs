using System.Text;
using IsoForge.Core;
using IsoForge.Core.Linux;
using IsoForge.Models;

// =====================================================================
// Suíte do lado Linux. Sem ISO baixada, sem rede: o que precisa de uma
// ISO de origem ela mesma fabrica com o xorriso.
// =====================================================================

var falhas = 0;
var total = 0;

void Check(bool ok, string descricao)
{
    total++;
    if (ok) { Console.WriteLine($"[OK]  {descricao}"); return; }
    falhas++;
    Console.WriteLine($"[FALHA] {descricao}");
}

void Secao(string titulo)
{
    Console.WriteLine();
    Console.WriteLine($"--- {titulo} ---");
}

var trabalho = Path.Combine(Path.GetTempPath(), "isoforge-teste-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(trabalho);
Console.WriteLine($"IsoForge — suíte Linux");
Console.WriteLine($"Sistema: {Environment.OSVersion}");
Console.WriteLine($"Pasta de trabalho: {trabalho}");

// ---------------------------------------------------------------------
Secao("Plataforma");

Check(Plataforma.EhLinux, "reconhece que está rodando no Linux");
Check(!Plataforma.EhWindows, "e que não está no Windows");
Check(Plataforma.NoCaminho("sh") != null, "acha o 'sh' no PATH");
Check(Plataforma.NoCaminho("nao-existe-mesmo-isto-aqui") == null, "não inventa executável que não existe");

var xorriso = IsoTools.FindXorriso();
Check(xorriso != null, $"acha o xorriso ({xorriso ?? "NÃO ENCONTRADO"})");
if (xorriso == null)
{
    Console.WriteLine("Sem xorriso não há o que testar adiante. Instale com 'sudo apt install xorriso'.");
    return 1;
}

// ---------------------------------------------------------------------
Secao("Cofre da configuração (sem DPAPI)");

var casa = Path.Combine(trabalho, "casa");
Directory.CreateDirectory(casa);
Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", casa);
Environment.SetEnvironmentVariable("HOME", casa);

try
{
    var claro = Encoding.UTF8.GetBytes("usuário=suporte;senha=segredo-que-não-pode-vazar");
    var cifrado = Cofre.Cifrar(claro);
    Check(!cifrado.SequenceEqual(claro), "o texto cifrado não é o texto claro");
    Check(Encoding.UTF8.GetString(cifrado).Contains("segredo") == false, "a senha não aparece em claro no arquivo");
    Check(Cofre.Decifrar(cifrado).SequenceEqual(claro), "decifra de volta exatamente o que entrou");

    // A chave é o segredo inteiro: se ela ficar legível por outros usuários, não protegeu nada.
    var chave = Path.Combine(casa, "IsoForge", "chave.bin");
    Check(File.Exists(chave), "criou o arquivo de chave");
    if (File.Exists(chave))
    {
        var modo = File.GetUnixFileMode(chave);
        Check(modo == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
            $"a chave está com permissão 0600 (está {modo})");
    }

    var adulterado = (byte[])cifrado.Clone();
    adulterado[^1] ^= 0xFF;
    var detectou = false;
    try { Cofre.Decifrar(adulterado); } catch { detectou = true; }
    Check(detectou, "recusa um arquivo adulterado em vez de devolver lixo");
}
catch (Exception ex)
{
    Check(false, "cofre: " + ex.Message);
}

// ---------------------------------------------------------------------
Secao("Arquivos de resposta das 9 distribuições");

BuildConfig Base(TargetOs os) => new()
{
    Os = os,
    UserName = "suporte",
    Password = "Senha#Forte123",
    ComputerName = "maquina-teste",
    OutputIsoPath = Path.Combine(trabalho, "saida.iso"),
    Linux = new LinuxConfig
    {
        FullName = "Suporte de TI",
        Timezone = "America/Sao_Paulo",
        LocaleId = "pt_BR.UTF-8",
        KeyboardLayout = "br",
        KeyboardVariant = "abnt2",
        ExtraPackages = "htop git"
    }
};

foreach (var info in OsCatalog.All.Where(o => o.IsLinux))
{
    var cfg = Base(info.Id);
    try
    {
        var arquivos = LinuxAnswerFile.Generate(cfg);
        var principal = arquivos.FirstOrDefault(a => a.RelativePath.EndsWith(info.AnswerFileName, StringComparison.Ordinal))
                        ?? arquivos.First();
        var conteudo = principal.Content;

        Check(conteudo.Length > 0, $"{info.Name}: gera {info.AnswerFileName}");
        Check(!conteudo.Contains('\r'), $"{info.Name}: sem CRLF (quebraria a execução no Linux)");
        Check(conteudo.Contains("suporte"), $"{info.Name}: o usuário entrou no arquivo");
        Check(!conteudo.Contains("Senha#Forte123"),
            $"{info.Name}: a senha NÃO aparece em texto puro (vai como hash)");
    }
    catch (Exception ex)
    {
        Check(false, $"{info.Name}: {ex.Message}");
    }
}

// ---------------------------------------------------------------------
Secao("Somente gerar arquivos (dry run)");

{
    var destino = Path.Combine(trabalho, "dryrun");
    Directory.CreateDirectory(destino);
    var cfg = Base(TargetOs.Ubuntu);
    new LinuxIsoPipeline(new Progress<string>(_ => { })).DryRun(cfg, destino);
    var gerados = Directory.GetFiles(destino, "*", SearchOption.AllDirectories);
    Check(gerados.Length > 0, $"grava arquivos na pasta escolhida ({gerados.Length} arquivos)");
    Check(gerados.Any(f => Path.GetFileName(f) == "user-data"), "o user-data do autoinstall está entre eles");

    foreach (var f in gerados.Where(f => f.EndsWith(".sh", StringComparison.Ordinal)))
    {
        var bytes = File.ReadAllBytes(f);
        Check(!(bytes.Length > 2 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF),
            $"{Path.GetFileName(f)}: sem BOM (um BOM antes do #! quebra o shebang)");
    }
}

// ---------------------------------------------------------------------
Secao("ISO seed, gerada de verdade pelo xorriso");

{
    var cfg = Base(TargetOs.Ubuntu);
    cfg.Linux.Delivery = LinuxDeliveryMode.SeedIso;
    cfg.OutputIsoPath = Path.Combine(trabalho, "seed.iso");

    var log = new List<string>();
    try
    {
        await new LinuxIsoPipeline(new Progress<string>(log.Add)).BuildAsync(cfg, CancellationToken.None);
        Check(File.Exists(cfg.OutputIsoPath), "a ISO seed foi criada");
        if (File.Exists(cfg.OutputIsoPath))
        {
            Check(new FileInfo(cfg.OutputIsoPath).Length > 0, "e não saiu vazia");
            Check(IsoTools.RotuloDeIso(cfg.OutputIsoPath) == LinuxAnswerFile.SeedLabel(TargetOs.Ubuntu),
                $"o rótulo é {LinuxAnswerFile.SeedLabel(TargetOs.Ubuntu)} — é por ele que o instalador a encontra");
            Check((await Dentro(cfg.OutputIsoPath)).Any(n => n.EndsWith("user-data", StringComparison.Ordinal)),
                "o user-data está dentro da imagem");
        }
    }
    catch (Exception ex)
    {
        Check(false, "geração da ISO seed: " + ex.Message);
        foreach (var l in log.TakeLast(10)) Console.WriteLine("    " + l);
    }
}

// ---------------------------------------------------------------------
Secao("Reempacotar uma ISO de origem");

{
    // Fabrica uma "ISO oficial" mínima: uma árvore com uma imagem de boot EFI e um grub.cfg,
    // que é o bastante para o pipeline extrair, remendar o boot e recompilar.
    var origemArvore = Path.Combine(trabalho, "origem");
    Directory.CreateDirectory(Path.Combine(origemArvore, "boot", "grub"));
    Directory.CreateDirectory(Path.Combine(origemArvore, "EFI", "boot"));
    File.WriteAllBytes(Path.Combine(origemArvore, "EFI", "boot", "efi.img"), new byte[1024 * 1024]);
    File.WriteAllText(Path.Combine(origemArvore, "boot", "grub", "grub.cfg"),
        "menuentry \"Instalar\" {\n    linux /casper/vmlinuz quiet splash ---\n    initrd /casper/initrd\n}\n");
    Directory.CreateDirectory(Path.Combine(origemArvore, "casper"));
    File.WriteAllText(Path.Combine(origemArvore, "casper", "vmlinuz"), "falso");
    File.WriteAllText(Path.Combine(origemArvore, "casper", "initrd"), "falso");

    var origemIso = Path.Combine(trabalho, "origem.iso");
    var argsIso = "-as mkisofs -iso-level 3 -joliet -rational-rock -volid \"TESTE-ORIGEM\" " +
               "-e EFI/boot/efi.img -no-emul-boot -isohybrid-gpt-basdat " +
               $"-o \"{origemIso}\" \"{origemArvore}\"";
    var codigo = await IsoTools.RunAsync(xorriso, argsIso, _ => { }, CancellationToken.None);
    Check(codigo == 0 && File.Exists(origemIso), "fabricou a ISO de origem de teste");

    if (File.Exists(origemIso))
    {
        Check(IsoTools.RotuloDeIso(origemIso) == "TESTE-ORIGEM", "lê o rótulo do volume sem montar a imagem");

        var extraida = Path.Combine(trabalho, "extraida");
        var rotulo = await IsoTools.AbrirIsoAsync(origemIso, extraida, _ => { }, CancellationToken.None);
        Check(rotulo == "TESTE-ORIGEM", "AbrirIsoAsync devolve o rótulo");
        Check(File.Exists(Path.Combine(extraida, "boot", "grub", "grub.cfg")),
            "AbrirIsoAsync extraiu o conteúdo sem precisar de root");

        var cfg = Base(TargetOs.Ubuntu);
        cfg.Linux.Delivery = LinuxDeliveryMode.Repack;
        cfg.SourceIsoPath = origemIso;
        cfg.OutputIsoPath = Path.Combine(trabalho, "repack.iso");

        var log = new List<string>();
        try
        {
            await new LinuxIsoPipeline(new Progress<string>(log.Add)).BuildAsync(cfg, CancellationToken.None);
            Check(File.Exists(cfg.OutputIsoPath), "a ISO reempacotada foi criada");
            if (File.Exists(cfg.OutputIsoPath))
            {
                var dentro = await Dentro(cfg.OutputIsoPath);
                Check(dentro.Any(n => n.EndsWith("user-data", StringComparison.Ordinal)),
                    "o arquivo de resposta entrou na imagem");
                Check(dentro.Any(n => n.Contains("grub.cfg", StringComparison.Ordinal)),
                    "o grub.cfg original continua lá");

                // O remendo do bootloader é o que faz a instalação ser desassistida de fato:
                // sem os parâmetros na linha do kernel, o instalador para e pergunta.
                var grubExtraido = Path.Combine(trabalho, "conferencia");
                await IsoTools.AbrirIsoAsync(cfg.OutputIsoPath, grubExtraido, _ => { }, CancellationToken.None);
                var grub = Path.Combine(grubExtraido, "boot", "grub", "grub.cfg");
                var texto = File.Exists(grub) ? File.ReadAllText(grub) : "";
                Check(texto.Contains("autoinstall", StringComparison.Ordinal),
                    "o menu do GRUB saiu com os parâmetros da instalação automática");
            }
        }
        catch (Exception ex)
        {
            Check(false, "reempacotamento: " + ex.Message);
            foreach (var l in log.TakeLast(12)) Console.WriteLine("    " + l);
        }
    }
}

// ---------------------------------------------------------------------
Secao("Limpeza");

IsoTools.ForceDeleteDirectory(trabalho, _ => { });
Check(!Directory.Exists(trabalho), "apaga a pasta de trabalho sem depender do 'rd' do Windows");

// ---------------------------------------------------------------------
Console.WriteLine();
if (falhas == 0)
{
    Console.WriteLine($"TODOS OS TESTES PASSARAM ({total} verificações)");
    return 0;
}
Console.WriteLine($"{falhas} de {total} verificações FALHARAM");
return 1;

// ---------------------------------------------------------------------

/// <summary>Nomes dos arquivos dentro de uma ISO, pelo xorriso.</summary>
async Task<string[]> Dentro(string iso)
{
    var (codigo, saida) = await IsoTools.TryRunCapturedAsync(
        xorriso!, $"-indev \"{iso}\" -find / -exec echo --", CancellationToken.None);
    return codigo != 0
        ? Array.Empty<string>()
        : saida.Split('\n').Select(l => l.Trim().Trim('\'')).Where(l => l.Length > 0).ToArray();
}
