# Operação dos pipelines CircleCI

Os pipelines abaixo usam YAMLs independentes. Cadastre cada caminho em **Project Settings → Pipelines** no CircleCI, usando a branch `main`:

| Pipeline | Config path | Uso |
|---|---|---|
| Saúde dos catálogos | `.circleci/catalog-health.yml` | Execute manualmente com `run_catalog_health=true`. O padrão verifica uma amostra rotativa de 500 mídias; `max_media_urls=0` verifica todas. Não precisa de credenciais. |
| Snapshots/recuperação | `.circleci/catalog-recovery.yml` | Desativado; removido para priorizar somente os catálogos V2 ativos. |
| Captura automática de screenshots | `.circleci/screenshots.yml` | Use `run_screenshot_sync=true`; o lote usa `screenshot_batch_size`. Encontra screenshots nas homepages e atualiza somente apps afetados. Requer `r2-publishing`. |
| Captura manual/automática de mídia | `.circleci/catalog-media.yml` | Use `run_catalog_media=true`; `media_mode=manual|auto`, `asset_type=icon|screenshot`. No modo automático, `scan_prefix` limita a busca a `0-9` ou a uma letra de `a` a `z` (vazio percorre tudo); `publish_batch_size` define o checkpoint dos ícones (padrão 200) e `batch_size` limita o total da execução. Para o modo manual, use `package_id` e `media_url`; screenshots em lote aceitam `media_urls` separadas por espaços/quebras de linha ou como array JSON. Reenviar uma captura pelo mesmo nome normalizado atualiza o arquivo publicado. Requer `r2-publishing`. |
| Índice de screenshots existentes | `.circleci/homepage-screenshots.yml` | Desativado; screenshots associadas aos apps são consultadas no catálogo V2. |
| Verificação do atualizador | `.circleci/updater-verification.yml` | Agende ou execute manualmente com `run_updater_verification=true`. Compila, abre a janela WPF e exercita o atualizador em uma VM descartável. Requer WinGet disponível na imagem Windows. |
| Validação do Release | `.circleci/release-validation.yml` | Associado à publicação de tags estáveis/pré-lançamentos `vX.Y.Z`; confere os assets publicados, instala e desinstala o pacote. |

No modo automático de ícones, `catalog-media.yml` consulta a CDN de ícones do WinGet, seleciona apps sem ícone no catálogo e publica um lote por execução (padrão: 100). Os ícones são normalizados para PNG em `media/icon.png`. O pipeline legado `icons.yml` permanece em modo de simulação se `publish_icons` não for definido como `true`.

O pipeline WinGet publica o catálogo hierárquico em `Store/Catalog/`: `manifest.json`, `manifest/search-index.json` e um JSON completo por app em `apps/{0-9|a-z}/{Publisher}/{Produto}/app.json`. A Microsoft Store publica um catálogo independente em `Store/Catalog/msstore/`, com seus próprios manifestos e índice, registros em `apps/{0-9|a-z}/{ProductId}/app.json` e mídia em cada pasta do app. O app combina os índices e busca os detalhes na origem indicada. Os caminhos são estáveis; cada pipeline envia detalhes e índices antes de publicar seu manifesto. `apps.json` é um intermediário local; apenas `Store/Database/metrics-cache.json` permanece como cache operacional.

O pipeline legado `.circleci/homepage-screenshots.yml` foi desativado. Screenshots novas são publicadas diretamente na mídia dos apps em `Store/Catalog/`.

Ao regenerar o catálogo WinGet, `winget-sync.yml` carrega `manifest/media-index.json` e mantém os assets associados durante a montagem Python. Capturas novas são publicadas pelo fluxo de mídia nos diretórios `media/` dos apps; ele atualiza `app.json`, índice e manifesto sem rodar o Indexer. Um ID ainda ausente fica no `media-index.json` e é incorporado quando o catálogo WinGet o descobrir.

## Snapshot e restauração

O pipeline de snapshots foi desativado. O R2 mantém somente as chaves estáveis do catálogo e da API V2; backups devem ser guardados fora desse bucket.

## GitHub Actions de segurança

`dependency-review.yml` revisa dependências novas em pull requests. Os PRs automáticos do Dependabot foram desativados; `codeql.yml` continua analisando C#, JavaScript/TypeScript e Python semanalmente, em PRs e em pushes relevantes para `main`.
