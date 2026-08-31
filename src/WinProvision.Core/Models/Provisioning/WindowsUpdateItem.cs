namespace WinProvision.Core.Models.Provisioning;

/// <summary>
/// Um item retornado pela busca do Windows Update Agent (WUAPI) — cobre tanto atualizações
/// de qualidade/segurança (<see cref="IsDriver"/> = false) quanto de driver (a mesma API do
/// Windows Update lista os dois tipos juntos, diferenciados pelo campo <c>Type</c> do
/// objeto COM). Usado só para exibir/relatar (ex.: preview do botão "Verificar agora"); a
/// instalação em si trabalha direto com os objetos COM originais dentro de
/// <see cref="Services.Provisioning.WindowsUpdateService"/>, não com este DTO.
/// </summary>
public record WindowsUpdateItem(
    string Title,
    IReadOnlyList<string> KbArticleIds,
    bool IsDriver,
    long MaxDownloadSizeBytes);
