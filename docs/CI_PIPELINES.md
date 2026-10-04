# Pipelines de catálogo e mídia

Os trabalhos de publicação no R2 têm uma área por função. Cada workflow do CircleCI roda sem `requires` de outro workflow: uma falha no Office, nos ícones ou no WinGet não cancela os demais. O arquivo já usado pelo projeto continua em `.circleci/config.yml`.

| Função | Configuração | Parâmetro manual | Destino |
| --- | --- | --- | --- |
| Catálogo Microsoft Store | `.circleci/config.yml` | `run_msstore_catalog=true` | `Store/Catalog/msstore/` e `Store/Api/msstore/` (manifestos, índice, apps e mídia por app) |
| Assets e ofertas Office | `.circleci/office.yml` | `run_office_assets=true` | `Office/Database/` e assets |
| Catálogo WinGet e API | `.circleci/winget-sync.yml` | `run_winget_catalog=true` | `Store/Catalog/` e `Store/Api/winget/` (mantém somente `Store/Database/metrics-cache.json` como cache do Indexer) |
| Captura automática de screenshots | `.circleci/screenshots.yml` | `run_screenshot_sync=true` | `Store/Catalog/apps/.../media/screenshots/` |
| Ícones da CDN WinGet | `.circleci/icons.yml` | `run_icon_sync=true`; `publish_icons=true` para publicar | `Store/Catalog/apps/.../media/icon.png` |
| Mídia manual ou captura direcionada | `.circleci/catalog-media.yml` | `run_catalog_media=true`, `media_mode=manual|auto`, `asset_type=icon|screenshot` | pasta `media/` do app e JSON/índices associados |
| Manifesto de ícones | `.circleci/icon-manifest.yml` | `run_icon_manifest=true` | `Store/icon-manifest.json` |
| Índice de screenshots já armazenadas | `.circleci/homepage-screenshots.yml` | `run_screenshot_index=true` (os gatilhos antigos continuam aceitos) | `Store/Database/screenshot-index.json` |

O contexto `r2-publishing` precisa conter `R2_ACCOUNT_ID`, `R2_ACCESS_KEY_ID` e `R2_SECRET_ACCESS_KEY`; `R2_BUCKET` é opcional (padrão `winprovision`). Para o indexador WinGet, `GITHUB_TOKEN` com acesso de leitura à API pública do GitHub é recomendado para evitar limites de requisições. Nunca colocar essas credenciais nos YAMLs.

## Ativação no CircleCI GitHub App

1. Enviar os YAMLs ao `main`. Em **Project Settings > Project Setup**, manter o pipeline atual apontando para `.circleci/config.yml`. Adicionar os pipelines **Ofertas Office** (`.circleci/office.yml`), **Catálogo WinGet** (`.circleci/winget-sync.yml`), **Mídia: catálogo** (`.circleci/catalog-media.yml`), **Mídia: ícones** (`.circleci/icons.yml`) e **Mídia: manifesto legado** (`.circleci/icon-manifest.yml`), usando o mesmo repositório como fonte de configuração e checkout.
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

O cliente da loja baixa `Store/Catalog/manifest.json`, carrega `Store/Catalog/manifest/search-index.json` e busca detalhes WinGet em `Store/Catalog/apps/{0-9|a-z}/{Publisher}/{Produto}/app.json`. O subcatálogo Microsoft Store é independente em `Store/Catalog/msstore/`: `manifest.json`, índices próprios e `apps/{0-9|a-z}/{ProductId}/app.json`, com as imagens em `media/`. A busca combina os índices; cada detalhe continua sendo carregado do catálogo de origem. As chaves são estáveis e os manifestos são publicados por último.

A API própria tem o manifesto raiz `Store/Api/manifest.json` e dois subcatálogos independentes (`winget/` e `msstore/`). Cada um contém `manifest.json`, `manifest/search-index.json` e `apps/{0-9|a-z}/{Publisher}/{Produto}/package.json`; o índice leve é carregado para ambas as origens e o manifesto de instaladores é consultado sob demanda. Cada pipeline atualiza somente sua origem.
Os workflows atuais publicam apenas esse formato; os objetos antigos em `Store/Api/v1/` ficam preservados no R2 para permitir retorno temporário ao cliente anterior e podem ser removidos depois que o app atualizado estiver validado.
Na implantação inicial, executar os workflows WinGet e Microsoft Store em `main` antes de usar o cliente atualizado; o manifesto raiz passa a anunciar cada origem quando ela termina de publicar.

Os ícones e screenshots novos ficam junto ao app: `Catalog/apps/{0-9|a-z}/{publisher}/{produto}/media/`. O JSON do app e o índice leve recebem os caminhos relativos. O workflow de mídia atualiza o detalhe, o índice e o manifesto diretamente, sem executar o Indexer; IDs que ainda não existem no catálogo ficam registrados em `manifest/media-index.json` e entram no `app.json` na próxima sincronização WinGet. O modo automático percorre a CDN WinGet para ícones e homepages para screenshots. O modo manual recebe o ID do pacote, a categoria da mídia e uma URL direta ou, para screenshots, várias URLs diretas em `media_urls`, separadas por espaços/quebras de linha ou como array JSON. Se o CircleCI achatar as quebras de linha, a separação por espaços também é aceita. Reenviar a mesma imagem pelo mesmo nome normalizado atualiza o arquivo existente; o ícone sempre usa o caminho fixo e também é atualizado. Os links publicados recebem uma versão baseada no hash para que clientes não continuem usando a imagem em cache após a atualização.

`apps.json` é um intermediário local do Indexer e não é publicado no R2. O workflow usa `Store/Catalog/manifest/search-index.json` para validar retenção e reaproveitar tamanhos de instaladores. `Store/Database/metrics-cache.json` permanece como cache operacional do Indexer. O catálogo Microsoft Store usa `Store/Catalog/msstore/` como fonte única.

O pipeline `.circleci/homepage-screenshots.yml` reindexa as imagens já armazenadas. `run_homepage_screenshot_sync`, `run_screenshot_sync`, `homepage_screenshot_batch_size`, `homepage_screenshot_package_id` e `homepage_screenshot_url` continuam aceitos por compatibilidade com gatilhos existentes; os parâmetros de lote/URL não são usados nessa reindexação. Para descobrir novas imagens em páginas oficiais, esse trabalho deve permanecer separado e pontual, fora da sincronização diária do catálogo.
