using System.Xml.Linq;

namespace IsoForge.Core;

/// <summary>
/// Utilitários para o Configuration.xml do Office Deployment Tool.
/// </summary>
public static class OfficeConfig
{
    /// <summary>
    /// Devolve o XML de configuração com o atributo SourcePath definido no elemento
    /// &lt;Add&gt; (instalação offline a partir de uma pasta local). Também usado para
    /// gerar o config de /download (SourcePath = pasta de destino do download).
    /// </summary>
    public static string WithSourcePath(string configXml, string sourcePath, string? version = null)
    {
        try
        {
            var doc = XDocument.Parse(configXml);
            var add = doc.Root?.Element("Add");
            if (add == null)
            {
                add = new XElement("Add");
                doc.Root!.AddFirst(add);
            }
            add.SetAttributeValue("SourcePath", sourcePath);
            // Fixa a versão baixada: sem isso o ODT consulta o CDN para achar a versão
            // "atual" do Channel e tenta baixá-la — falhando offline mesmo com a fonte local.
            if (!string.IsNullOrWhiteSpace(version))
                add.SetAttributeValue("Version", version);
            return doc.ToString();
        }
        catch
        {
            // Fallback simples se o XML não puder ser parseado.
            var attrs = $"SourcePath=\"{sourcePath}\"" + (string.IsNullOrWhiteSpace(version) ? "" : $" Version=\"{version}\"");
            return configXml.Contains("SourcePath=")
                ? configXml
                : configXml.Replace("<Add ", $"<Add {attrs} ");
        }
    }

    /// <summary>Descobre a versão baixada (nome da pasta em Office\Data\&lt;versão&gt;, ex.: 16.0.17928.20216).</summary>
    public static string? DetectVersion(string officeSourceFolder)
    {
        try
        {
            var dataDir = System.IO.Path.Combine(officeSourceFolder, "Office", "Data");
            if (!System.IO.Directory.Exists(dataDir)) return null;
            return System.IO.Directory.GetDirectories(dataDir)
                .Select(d => System.IO.Path.GetFileName(d))
                .Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d+\.\d+\.\d+\.\d+$"))
                .OrderBy(n => n)
                .LastOrDefault();
        }
        catch { return null; }
    }
}
