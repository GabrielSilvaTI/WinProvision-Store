# Operação dos pipelines CircleCI

Os pipelines abaixo usam YAMLs independentes. Cadastre cada caminho em **Project Settings → Pipelines** no CircleCI, usando a branch `main`:

| Pipeline | Config path | Uso |
|---|---|---|
| Saúde dos catálogos | `.circleci/catalog-health.yml` | Execute manualmente com `run_catalog_health=true`. O padrão verifica uma amostra rotativa de 500 mídias; `max_media_urls=0` verifica todas. Não precisa de credenciais. |
| Snapshots/recuperação | `.circleci/catalog-recovery.yml` | Faça snapshots semanais ou antes de mudanças de catálogo de maior risco com `run_catalog_snapshot=true`. Para restaurar, rode manualmente com `restore_catalog_snapshot=true` e `catalog_snapshot_id=AAAAMMDDTHHMMSSZ`. |
| Ingestão de screenshots curados | `.circleci/screenshots.yml` | Use `run_screenshot_sync=true` para adicionar conteúdo novo à base de imagens. Requer o contexto `r2-publishing`. |
| Índice de screenshots existentes | `.circleci/homepage-screenshots.yml` | Agendamentos antigos continuam funcionando; para novos gatilhos, use `run_screenshot_index=true`. Essa rotina apenas lista objetos já existentes no R2 e publica `Store/Database/screenshot-index.json`. Requer `r2-publishing`. |
| Verificação do atualizador | `.circleci/updater-verification.yml` | Agende ou execute manualmente com `run_updater_verification=true`. Compila, abre a janela WPF e exercita o atualizador em uma VM descartável. Requer WinGet disponível na imagem Windows. |
| Validação do Release | `.circleci/release-validation.yml` | Associado à publicação de tags estáveis/pré-lançamentos `vX.Y.Z`; confere os assets publicados, instala e desinstala o pacote. |

O pipeline WinGet publica o catálogo v2 em `Store/Catalog/v2/`: um manifesto, um índice de busca e um JSON completo por app em uma hierarquia de pastas. O manifesto aponta para um release imutável identificado pelo hash e é publicado por último. O cliente carrega o índice leve e busca detalhes ao abrir um app. `Store/Database/apps.json` ainda é gerado para compatibilidade com as rotinas antigas durante a transição.

O pipeline WinGet também atualiza o índice de screenshots em seguida à publicação do catálogo e do cache/API. O índice contém somente os IDs com arquivos encontrados em `Store/Screenshot_Database/`. A leitura usa paginação S3 do R2 (aproximadamente uma chamada por mil objetos), sem buscar páginas dos aplicativos e sem baixar as imagens outra vez. A rotina `.circleci/homepage-screenshots.yml` é uma recuperação manual/agendada compatível que reconstrói o mesmo índice.

Ao regenerar o catálogo WinGet, `winget-sync.yml` preserva screenshots existentes, monta os JSONs v2 com o utilitário Python e publica os arquivos do release antes do manifesto. O fluxo curado de screenshots também remonta o release v2 depois de atualizar os metadados.

## Snapshot e restauração

Os snapshots são cópias dos JSONs ativos, gravadas em `Store/Recovery/Snapshots/<id>/` no bucket público que já contém esses catálogos. Como cada snapshot cria uma cópia completa e não há deduplicação entre snapshots, evite agendamento diário; prefira semanal ou antes de publicações arriscadas. Os objetos guardados são dados já públicos; os snapshots não incluem credenciais nem código-fonte. Cada cópia tem um manifesto com SHA-256 e tamanho. O manifesto só é enviado depois que todos os arquivos são lidos e validados, e a restauração verifica o snapshot inteiro antes de alterar os objetos ativos.

A restauração é uma operação manual e substitui os JSONs ativos. Os snapshots não são apagados automaticamente; defina uma regra de retenção no Cloudflare R2 se quiser limitar o histórico.

## GitHub Actions de segurança

`dependency-review.yml` revisa dependências novas em pull requests. Os PRs automáticos do Dependabot foram desativados; `codeql.yml` continua analisando C#, JavaScript/TypeScript e Python semanalmente, em PRs e em pushes relevantes para `main`.
