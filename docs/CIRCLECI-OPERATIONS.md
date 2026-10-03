# Operação dos pipelines CircleCI

Os pipelines abaixo usam YAMLs independentes. Cadastre cada caminho em **Project Settings → Pipelines** no CircleCI, usando a branch `main`:

| Pipeline | Config path | Uso |
|---|---|---|
| Saúde dos catálogos | `.circleci/catalog-health.yml` | Execute manualmente com `run_catalog_health=true`. O padrão verifica uma amostra rotativa de 500 mídias; `max_media_urls=0` verifica todas. Não precisa de credenciais. |
| Snapshots/recuperação | `.circleci/catalog-recovery.yml` | Faça snapshots semanais ou antes de mudanças de catálogo de maior risco com `run_catalog_snapshot=true`. Para restaurar, rode manualmente com `restore_catalog_snapshot=true` e `catalog_snapshot_id=AAAAMMDDTHHMMSSZ`. |
| Captura automática de screenshots | `.circleci/screenshots.yml` | Use `run_screenshot_sync=true`; o lote usa `screenshot_batch_size`. Encontra screenshots nas homepages e atualiza somente apps afetados. Requer `r2-publishing`. |
| Captura manual/automática de mídia | `.circleci/catalog-media.yml` | Use `run_catalog_media=true`; `media_mode=manual|auto`, `asset_type=icon|screenshot`, `package_id` e `media_url` para captura manual. Requer `r2-publishing`. |
| Índice de screenshots existentes | `.circleci/homepage-screenshots.yml` | Agendamentos antigos continuam funcionando; para novos gatilhos, use `run_screenshot_index=true`. Essa rotina apenas lista objetos já existentes no R2 e publica `Store/Database/screenshot-index.json`. Requer `r2-publishing`. |
| Verificação do atualizador | `.circleci/updater-verification.yml` | Agende ou execute manualmente com `run_updater_verification=true`. Compila, abre a janela WPF e exercita o atualizador em uma VM descartável. Requer WinGet disponível na imagem Windows. |
| Validação do Release | `.circleci/release-validation.yml` | Associado à publicação de tags estáveis/pré-lançamentos `vX.Y.Z`; confere os assets publicados, instala e desinstala o pacote. |

O pipeline WinGet publica o catálogo hierárquico em `Store/Catalog/`: `manifest.json`, `manifest/search-index.json` e um JSON completo por app em `apps/{0-9|a-z}/{Publisher}/{Produto}/app.json`. Os caminhos são estáveis e não recebem uma pasta por hash. O pipeline valida a impressão digital dos inputs antes de gerar; se o catálogo ativo já corresponder aos mesmos inputs, ignora download, indexação e publicação. Em uma geração nova, apenas objetos alterados são enviados, e `manifest.json` é publicado por último. `Store/Database/apps.json` ainda é gerado para compatibilidade com rotinas antigas durante a transição.

O pipeline `.circleci/homepage-screenshots.yml` atualiza o índice de screenshots de forma independente. O índice contém somente os IDs com arquivos encontrados em `Store/Screenshot_Database/`. A leitura usa paginação S3 do R2 (aproximadamente uma chamada por mil objetos), sem buscar páginas dos aplicativos e sem baixar as imagens outra vez.

Ao regenerar o catálogo WinGet, `winget-sync.yml` carrega `manifest/media-index.json` e mantém os assets associados durante a montagem Python. Capturas novas são publicadas pelo fluxo de mídia nos diretórios `media/` dos apps; ele atualiza `app.json`, índice e manifesto sem rodar o Indexer. Um ID ainda ausente fica no `media-index.json` e é incorporado quando o catálogo WinGet o descobrir.

## Snapshot e restauração

Os snapshots são cópias dos JSONs ativos, gravadas em `Store/Recovery/Snapshots/<id>/` no bucket público que já contém esses catálogos. Como cada snapshot cria uma cópia completa e não há deduplicação entre snapshots, evite agendamento diário; prefira semanal ou antes de publicações arriscadas. Os objetos guardados são dados já públicos; os snapshots não incluem credenciais nem código-fonte. Cada cópia tem um manifesto com SHA-256 e tamanho. O manifesto só é enviado depois que todos os arquivos são lidos e validados, e a restauração verifica o snapshot inteiro antes de alterar os objetos ativos.

A restauração é uma operação manual e substitui os JSONs ativos. Os snapshots não são apagados automaticamente; defina uma regra de retenção no Cloudflare R2 se quiser limitar o histórico.

## GitHub Actions de segurança

`dependency-review.yml` revisa dependências novas em pull requests. Os PRs automáticos do Dependabot foram desativados; `codeql.yml` continua analisando C#, JavaScript/TypeScript e Python semanalmente, em PRs e em pushes relevantes para `main`.
