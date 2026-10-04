# Pipelines de catálogo e mídia

Os trabalhos de publicação no R2 têm uma área por função. Cada workflow do CircleCI roda sem `requires` de outro workflow: uma falha no Office, nos ícones ou no WinGet não cancela os demais. O arquivo já usado pelo projeto continua em `.circleci/config.yml`.

| Função | Configuração | Parâmetro manual | Destino |
| --- | --- | --- | --- |
| Catálogo Microsoft Store | `.circleci/config.yml` | `run_msstore_catalog=true` | `Store/Catalog/msstore/` (manifestos, índice, apps e mídia por app) |
| Assets e ofertas Office | `.circleci/office.yml` | `run_office_assets=true` | `Office/Database/` e assets |
| Catálogo WinGet e API | `.circleci/winget-sync.yml` | `run_winget_catalog=true` | `Store/Catalog/` (inclui URLs e opções de instalador nos detalhes; mantém `Store/Database/metrics-cache.json` como cache do Indexer) |
| Captura automática de screenshots | `.circleci/screenshots.yml` | `run_screenshot_sync=true` | `Store/Catalog/apps/.../media/screenshots/` |
| Ícones da CDN WinGet | `.circleci/icons.yml` | `run_icon_sync=true`; `publish_icons=true` para publicar | `Store/Catalog/apps/.../media/{produto}_icon.png` |
| Mídia manual ou captura direcionada | `.circleci/catalog-media.yml` | `run_catalog_media=true`, `media_mode=manual|auto`, `asset_type=icon|screenshot` | pasta `media/` do app e JSON/índices associados |
| Manifesto legado de ícones | `.circleci/icon-manifest.yml` | desativado | — |
| Índice legado de screenshots | `.circleci/homepage-screenshots.yml` | desativado | — |

O contexto `r2-publishing` precisa conter `R2_ACCOUNT_ID`, `R2_ACCESS_KEY_ID` e `R2_SECRET_ACCESS_KEY`; `R2_BUCKET` é opcional (padrão `winprovision`). Para o indexador WinGet, `GITHUB_TOKEN` com acesso de leitura à API pública do GitHub é recomendado para evitar limites de requisições. Nunca colocar essas credenciais nos YAMLs.

## Ativação no CircleCI GitHub App

1. Enviar os YAMLs ao `main`. Em **Project Settings > Project Setup**, manter o pipeline atual apontando para `.circleci/config.yml`. Adicionar os pipelines **Ofertas Office** (`.circleci/office.yml`), **Catálogo WinGet** (`.circleci/winget-sync.yml`), **Mídia: catálogo** (`.circleci/catalog-media.yml`) e **Mídia: ícones** (`.circleci/icons.yml`), usando o mesmo repositório como fonte de configuração e checkout. Os pipelines de manifesto legado e screenshots antigas estão desativados.
2. Executar manualmente cada função em `main` com o parâmetro da tabela. Para ícones, começar sem `publish_icons` (simulação); depois fazer uma execução real. No modo automático, `scan_prefix` pode limitar a busca a `0-9` ou uma letra de `a` a `z`; vazio significa catálogo inteiro. Para percorrer tudo, defina `batch_size` acima da contagem de apps. Os ícones são publicados e o cursor salvo em lotes de `publish_batch_size` (padrão 200), com progresso no log. Para captura manual, usar `media_mode=manual`, informar `package_id`, escolher `asset_type` e preencher `media_url`. Screenshots do mesmo app podem ser enviadas em lote em `media_urls`, separadas por espaços/quebras de linha ou como array JSON. O parser também aceita vários links colados em `media_url`. Verificar os relatórios em **Artifacts** e os objetos correspondentes no R2.
3. Criar gatilhos agendados separados em **Project Settings > Project Setup** para a branch `main`. Cada gatilho deve passar seu parâmetro; o agendamento de ícones também precisa de `publish_icons=true`. Sugestão de horários em UTC:

   | Função | Frequência | UTC | Brasília (UTC−3) |
   | --- | --- | --- | --- |
   | Microsoft Store | domingo | 03:00 | 00:00 |
   | Office | domingo | 05:00 | 02:00 |
   | WinGet | diariamente | 08:00 | 05:00 |
   | Ícones | terça e sexta | 11:00 | 08:00 |
   | Manifesto de ícones | terça e sexta | 13:00 | 10:00 |
   | Saúde dos catálogos (amostra rotativa de 500 mídias) | semanalmente ou manual | 17:00 | 14:00 |

4. Configure o índice de screenshots no pipeline independente `.circleci/homepage-screenshots.yml` (um gatilho por execução). Os catálogos Microsoft Store, Office e WinGet são publicados pelo CircleCI; evite configurar outro publicador para os mesmos destinos no GitHub Actions. O GitHub Actions continua responsável por build, release, Pages, lint, testes, backup e utilitários.

Microsoft Store, Office e WinGet têm pipelines separados; seus resultados aparecem independentemente no CircleCI. O WinGet não incorpora mais os registros Microsoft Store; o app combina os índices no carregamento.

O cliente usa os mesmos catálogos como catálogo de busca e fonte da API própria. O índice combinado vem de `Store/Catalog/manifest/search-index.json` (WinGet) e `Store/Catalog/msstore/manifest/search-index.json` (Microsoft Store); os detalhes são carregados de `app.json` na origem indicada. Cada detalhe WinGet contém `installers`, com URL HTTPS, SHA-256, tipo/formato, arquitetura, escopo e os argumentos silenciosos quando conhecidos. Não há cópia separada desses registros em `Store/Api/`.

O WinGet publica em `Store/Catalog/`; a Microsoft Store publica em `Store/Catalog/msstore/`. Cada pipeline atualiza seu catálogo, índice e manifesto. Os manifestos são publicados por último. Para a implantação do cliente atualizado, executar os dois workflows em `main`.

Os ícones e screenshots ficam junto ao app: `Catalog/apps/{0-9|a-z}/{publisher}/{produto}/media/`. Os nomes seguem `{produto}_icon.ext`, `{produto}_banner.ext` e `{produto}_screenshot_01.ext`. O workflow de mídia atualiza o detalhe, o índice e o manifesto diretamente, sem executar o Indexer; IDs que ainda não existem no catálogo ficam registrados em `manifest/media-index.json` e entram no `app.json` na próxima sincronização WinGet. O modo automático percorre a CDN WinGet para ícones e homepages para screenshots. O modo manual recebe o ID do pacote, a categoria da mídia e uma URL direta ou, para screenshots, várias URLs diretas em `media_urls`, separadas por espaços/quebras de linha ou como array JSON. Reenviar uma imagem pelo mesmo nome normalizado atualiza o arquivo existente.

`apps.json` é um intermediário local do Indexer e não é publicado no R2. O workflow usa `Store/Catalog/manifest/search-index.json` para validar retenção e reaproveitar tamanhos de instaladores. `Store/Database/metrics-cache.json` permanece como cache operacional do Indexer. O catálogo Microsoft Store usa `Store/Catalog/msstore/` como fonte única.

O pipeline `.circleci/homepage-screenshots.yml` reindexa as imagens já armazenadas. `run_homepage_screenshot_sync`, `run_screenshot_sync`, `homepage_screenshot_batch_size`, `homepage_screenshot_package_id` e `homepage_screenshot_url` continuam aceitos por compatibilidade com gatilhos existentes; os parâmetros de lote/URL não são usados nessa reindexação. Para descobrir novas imagens em páginas oficiais, esse trabalho deve permanecer separado e pontual, fora da sincronização diária do catálogo.
