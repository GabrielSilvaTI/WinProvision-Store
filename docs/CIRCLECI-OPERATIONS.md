# Operação dos pipelines CircleCI

Os pipelines abaixo usam YAMLs independentes. Cadastre cada caminho em **Project Settings → Pipelines** no CircleCI, usando a branch `main`:

| Pipeline | Config path | Uso |
|---|---|---|
| Saúde dos catálogos | `.circleci/catalog-health.yml` | Agende com `run_catalog_health=true`. `max_media_urls=0` verifica todas as mídias; um número positivo roda uma amostra semanal rotativa. Não precisa de credenciais. |
| Snapshots/recuperação | `.circleci/catalog-recovery.yml` | Agende `run_catalog_snapshot=true` diariamente. Para restaurar, rode manualmente com `restore_catalog_snapshot=true` e `catalog_snapshot_id=AAAAMMDDTHHMMSSZ`. |
| Screenshots WinGet | `.circleci/screenshots.yml` | Agende `run_screenshot_sync=true` depois da atualização do catálogo WinGet. Requer o contexto `r2-publishing`. |
| Capturas das páginas oficiais | `.circleci/homepage-screenshots.yml` | Agende `run_homepage_screenshot_sync=true`. `homepage_screenshot_batch_size` define o lote (padrão: 100). Para um caso específico, use `homepage_screenshot_package_id` ou `homepage_screenshot_url`. O nome antigo `run_screenshot_sync` é aceito temporariamente para não quebrar agendamentos existentes. Requer `r2-publishing`. |
| Verificação do atualizador | `.circleci/updater-verification.yml` | Agende ou execute manualmente com `run_updater_verification=true`. Compila, abre a janela WPF e exercita o atualizador em uma VM descartável. Requer WinGet disponível na imagem Windows. |
| Validação do Release | `.circleci/release-validation.yml` | Associado à publicação de tags estáveis/pré-lançamentos `vX.Y.Z`; confere os assets publicados, instala e desinstala o pacote. |

As duas rotinas de screenshots têm finalidades diferentes: `screenshots.yml` associa imagens curadas do banco UniGetUI aos IDs WinGet; `homepage-screenshots.yml` procura capturas nas páginas oficiais dos aplicativos. O crawler respeita `robots.txt`, processa lotes e publica no catálogo apenas imagens encontradas e validadas. Nem toda página oferece capturas em um formato que o crawler consiga identificar.

## Snapshot e restauração

Os snapshots são cópias dos JSONs ativos, gravadas em `Store/Recovery/Snapshots/<id>/` no bucket público que já contém esses catálogos. Os objetos guardados são dados já públicos; os snapshots não incluem credenciais nem código-fonte. Cada cópia tem um manifesto com SHA-256 e tamanho. O manifesto só é enviado depois que todos os arquivos são lidos e validados, e a restauração verifica o snapshot inteiro antes de alterar os objetos ativos.

A restauração é uma operação manual e substitui os JSONs ativos. Os snapshots não são apagados automaticamente; defina uma regra de retenção no Cloudflare R2 se quiser limitar o histórico.

## GitHub Actions de segurança

`dependency-review.yml` revisa dependências novas em pull requests. `dependabot.yml` verifica semanalmente dependências NuGet, pip e Actions. `codeql.yml` analisa C#, JavaScript/TypeScript e Python semanalmente, em PRs e em pushes para `main`.
