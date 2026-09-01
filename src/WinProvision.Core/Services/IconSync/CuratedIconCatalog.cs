namespace WinProvision.Core.Services.IconSync;

/// <summary>
/// Lote pequeno de ícones dos apps mais visíveis da Store, curados manualmente e
/// embutidos direto no .exe (ver WinProvision.Store/Assets/Icons/Apps, registrados
/// como Resource no .csproj). Consultado ANTES do banco comunitário remoto (ver
/// IconService.ResolveIconUrl) — garante esses ícones específicos independentemente
/// de rede, cache local ainda vazio ou do workflow remoto estar no ar.
///
/// Cada entrada exige recompilar/republicar o app para corrigir (diferente do banco
/// remoto, que atualiza sozinho no próximo sync) — por isso este catálogo deve ficar
/// restrito a ícones já validados visualmente, não ao lote inteiro em curadoria.
///
/// Os nomes de arquivo abaixo são os literais entregues (mesma convenção do banco
/// comunitário: nome = Package ID do winget). A chave de busca é
/// IconIdNormalizer.Normalize(nome-sem-extensão) - a mesma normalização já usada
/// para casar com AppEntry.Id, então um nome de arquivo que não corresponda a um Id
/// real do catálogo simplesmente nunca dá match (sem quebrar nada, só não é usado).
/// </summary>
public static class CuratedIconCatalog
{
    private static readonly string[] FileNames =
    [
        "7zip.7zip.png",
        "Adobe.Acrobat.Reader.64-bit.png",
        "AnyDeskSoftwareGmbH.AnyDesk.png",
        "Anysphere.Cursor.png",
        "BlenderFoundation.Blender.png",
        "Brave.Brave.png",
        "Discord.Discord.png",
        "Docker.DockerDesktop.png",
        "Git.Git.png",
        "GitHub.GitHubDesktop.png",
        "Google.Chrome.png",
        "HydraLauncher.Hydra.png",
        "LocalSend.LocalSend.png",
        "Microsoft.Edge.png",
        "Microsoft.PowerToys.png",
        "Microsoft.VisualStudio.2022.Community.png",
        "Microsoft.VisualStudioCode.png",
        "Mozilla.Firefox.png",
        "Notepad++.Notepad++.png",
        "Notion.Notion.png",
        "OBSProject.OBSStudio.png",
        "Opera.Opera.png",
        "Python.Python.3.12.png",
        "RARLab.WinRAR.png",
        "Spotify.Spotify.png",
        "Telegram.TelegramDesktop.png",
        "TheDocumentFoundation.LibreOffice.png",
        "Valve.Steam.png",
        "VideoLAN.VLC.png",
        "WhatsApp.WhatsApp.png",
    ];

    private static readonly Dictionary<string, string> ByNormalizedId = BuildLookup();

    private static Dictionary<string, string> BuildLookup()
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string fileName in FileNames)
        {
            string key = IconIdNormalizer.Normalize(fileName);
            if (key.Length > 0)
                lookup[key] = fileName;
        }

        return lookup;
    }

    /// <summary>
    /// Devolve a pack URI do ícone curado para este Id de app, se existir no lote
    /// embutido. Usa a mesma normalização do banco comunitário (IconIdNormalizer),
    /// então a comparação é tolerante a maiúsculas/minúsculas e pontuação.
    /// </summary>
    public static bool TryGetPackUri(string appId, out string packUri)
    {
        string normalized = IconIdNormalizer.Normalize(appId);
        if (normalized.Length > 0 && ByNormalizedId.TryGetValue(normalized, out var fileName))
        {
            packUri = $"pack://application:,,,/Assets/Icons/Apps/{fileName}";
            return true;
        }

        packUri = string.Empty;
        return false;
    }
}
