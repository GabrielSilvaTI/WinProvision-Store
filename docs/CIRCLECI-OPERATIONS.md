# Operação dos pipelines CircleCI

Os pipelines abaixo usam YAMLs independentes. Cadastre cada caminho em **Project Settings → Pipelines** no CircleCI, usando a branch `main`:

| Pipeline | Config path | Uso |
|---|---|---|
| Saúde dos catálogos | `.circleci/catalog-health.yml` | Agende com `run_catalog_health=true`. `max_media_urls=0` verifica todas as mídias; um número positivo roda uma amostra semanal rotativa. Não precisa de credenciais. |
| Snapshots/recuperação | `.circleci/catalog-recovery.yml` | Agende `run_catalog_snapshot=true` diariamente. Para restaurar, rode manualmente com `restore_catalog_snapshot=true` e `catalog_snapshot_id=AAAAMMDDTHHMMSSZ`. |
| Ingestão de screenshots curados | `.circleci/screenshots.yml` | Use `run_screenshot_sync=true` para adicionar conteúdo novo à base de imagens. Requer o contexto `r2-publishing`. |
| Índice de screenshots existentes | `.circleci/homepage-screenshots.yml` | Agendamentos antigos continuam funcionando; para novos gatilhos, use `run_screenshot_index=true`. Essa rotina apenas lista objetos já existentes no R2 e publica `Store/Database/screenshot-index.json`. Requer `r2-publishing`. |
| Verificação do atualizador | `.circleci/updater-verification.yml` | Agende ou execute manualmente com `run_updater_verification=true`. Compila, abre a janela WPF e exercita o atualizador em uma VM descartável. Requer WinGet disponível na imagem Windows. |
| Validação do Release | `.circleci/release-validation.yml` | Associado à publicação de tags estáveis/pré-lançamentos `vX.Y.Z`; confere os assets publicados, instala e desinstala o pacote. |

O pipeline WinGet executa a atualização do índice de screenshots em seguida à publicação do catálogo e do cache/API. O índice é separado do `apps.json` e contém somente os IDs com arquivos encontrados em `Store/Screenshot_Database/`. A leitura usa paginação S3 do R2 (aproximadamente uma chamada por mil objetos), sem buscar páginas dos aplicativos e sem baixar as imagens outra vez. A rotina `.circleci/homepage-screenshots.yml` é uma recuperação manual/agendada compatível que reconstrói o mesmo índice.

Ao regenerar o catálogo WinGet, `winget-sync.yml` preserva os campos antigos como compatibilidade e publica o índice compacto separadamente. O cliente combina as URLs do índice com os apps por ID, sem aumentar ou regravar os 16 mil registros do catálogo.

## Snapshot e restauração

Os snapshots são cópias dos JSONs ativos, gravadas em `Store/Recovery/Snapshots/<id>/` no bucket público que já contém esses catálogos. Os objetos guardados são dados já públicos; os snapshots não incluem credenciais nem código-fonte. Cada cópia tem um manifesto com SHA-256 e tamanho. O manifesto só é enviado depois que todos os arquivos são lidos e validados, e a restauração verifica o snapshot inteiro antes de alterar os objetos ativos.

A restauração é uma operação manual e substitui os JSONs ativos. Os snapshots não são apagados automaticamente; defina uma regra de retenção no Cloudflare R2 se quiser limitar o histórico.

## GitHub Actions de segurança

`dependency-review.yml` revisa dependências novas em pull requests. `dependabot.yml` verifica semanalmente dependências NuGet, pip e Actions. `codeql.yml` analisa C#, JavaScript/TypeScript e Python semanalmente, em PRs e em pushes para `main`.
