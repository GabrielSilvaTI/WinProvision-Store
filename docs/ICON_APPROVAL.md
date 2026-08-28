# Aprovação manual de ícones do Winstall

O [Winstall](https://winstall.app) organiza os ícones dos apps sob nomes de arquivo
próprios, que não necessariamente batem com o `PackageIdentifier` do winget usado no
restante do catálogo (ex.: `vscode.png` para `Microsoft.VisualStudioCode`). Por isso,
diferente das outras duas fontes de ícone (UniGetUI e o `package-icons` externo, que
usam match automático e confiável), o Winstall só entra na base publicada
(`icons-database.json`) através de um mapeamento aprovado manualmente.

## Como funciona

1. Toda execução do workflow `sync-icon-databases.yml` gera
   `winstall-review-candidates.json` como artefato de build — uma lista de sugestões
   (`arquivo do Winstall → app do catálogo`) calculada por similaridade de nome
   (índice de Jaccard entre tokens). Não é confiável o suficiente para publicar sozinha.
2. Um humano baixa esse artefato e revisa quais sugestões estão corretas.
3. As entradas confirmadas são adicionadas a
   [`src/WinProvision.Indexer/config/winstall-approved-mappings.json`](../src/WinProvision.Indexer/config/winstall-approved-mappings.json)
   via Pull Request, no formato:

   ```json
   {
     "Microsoft.VisualStudioCode": "vscode.png",
     "Mozilla.Firefox": "firefox.png"
   }
   ```

   A chave é o `PackageIdentifier` (o mesmo `Id` do `apps.json`); o valor é o caminho
   do arquivo relativo à pasta `public/assets/apps` do repositório do Winstall
   (inclua subpasta, ex. `fallback/algo.png`, se for o caso).

4. Na próxima execução do workflow, essas entradas passam a resolver ícone via
   Winstall — prioridade mais alta entre as três fontes comunitárias, por ser a
   única curada por humano. O manifesto oficial do WinGet (CDN + `index.db`,
   via `WinGetOfficialManifestRepository`) continua tendo prioridade ainda
   maior quando resolve o Id, já que vem direto da mesma infraestrutura que o
   `winget install` usa — o Winstall só entra pra preencher o que esse Tier 1
   não cobrir (manifesto sem a tag `Icons`, Id fora do `winget-pkgs`, etc.).

## Por que os candidatos nunca são aplicados automaticamente

Um catálogo público mostrando o ícone errado para um app é pior do que não mostrar
ícone nenhum (nesse caso cai no fallback de favicon do `IconService`). Similaridade
de texto entre nome de arquivo e nome de app tem falsos positivos previsíveis demais
(ex.: "Photo" vs "Photoshop", "Docker" vs "Docker Desktop") para publicar sem revisão.
