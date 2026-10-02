# Pipelines de catálogo e mídia

Os trabalhos de publicação no R2 têm uma área por função. Cada workflow do CircleCI roda sem `requires` de outro workflow: uma falha no Office, nos ícones ou no WinGet não cancela os demais. O arquivo já usado pelo projeto continua em `.circleci/config.yml`.

| Função | Configuração | Parâmetro manual | Destino |
| --- | --- | --- | --- |
| Catálogo Microsoft Store | `.circleci/config.yml` | `run_msstore_catalog=true` | `Store/Database/msstore-catalog.json` e assets |
| Assets e ofertas Office | `.circleci/office.yml` | `run_office_assets=true` | `Office/Database/` e assets |
| Catálogo WinGet e API | `.circleci/winget-sync.yml` | `run_winget_catalog=true` | `Store/Database/apps.json` e `Store/Api/v1/` |
| Ingestão de screenshots curados do WinGet | `.circleci/screenshots.yml` | `run_screenshot_sync=true` | `Store/Screenshot_Database/` |
| Ícones da CDN WinGet | `.circleci/icons.yml` | `run_icon_sync=true`; `publish_icons=true` para publicar | `Store/Icon_Database/` |
| Manifesto de ícones | `.circleci/icon-manifest.yml` | `run_icon_manifest=true` | `Store/icon-manifest.json` |
| Índice de screenshots já armazenadas | `.circleci/homepage-screenshots.yml` | `run_screenshot_index=true` (os gatilhos antigos continuam aceitos) | `Store/Database/screenshot-index.json` |

O contexto `r2-publishing` precisa conter `R2_ACCOUNT_ID`, `R2_ACCESS_KEY_ID` e `R2_SECRET_ACCESS_KEY`; `R2_BUCKET` é opcional (padrão `winprovision`). Para o indexador WinGet, `GITHUB_TOKEN` com acesso de leitura à API pública do GitHub é recomendado para evitar limites de requisições. Nunca colocar essas credenciais nos YAMLs.

## Ativação no CircleCI GitHub App

1. Enviar os YAMLs ao `main`. Em **Project Settings > Project Setup**, manter o pipeline atual apontando para `.circleci/config.yml`. Adicionar os pipelines **Ofertas Office** (`.circleci/office.yml`), **Catálogo WinGet** (`.circleci/winget-sync.yml`), **Mídia: ícones** (`.circleci/icons.yml`) e **Mídia: manifesto** (`.circleci/icon-manifest.yml`), usando o mesmo repositório como fonte de configuração e checkout.
2. Executar manualmente cada função em `main` com o parâmetro da tabela. Para ícones, começar sem `publish_icons` (simulação); depois fazer uma execução real. Verificar os relatórios em **Artifacts** e os objetos correspondentes no R2.
3. Criar gatilhos agendados separados em **Project Settings > Project Setup** para a branch `main`. Cada gatilho deve passar seu parâmetro; o agendamento de ícones também precisa de `publish_icons=true`. Sugestão de horários em UTC:

   | Função | Frequência | UTC | Brasília (UTC−3) |
   | --- | --- | --- | --- |
   | Microsoft Store | domingo | 03:00 | 00:00 |
   | Office | domingo | 05:00 | 02:00 |
   | WinGet | diariamente | 08:00 | 05:00 |
   | Ícones | terça e sexta | 11:00 | 08:00 |
   | Manifesto de ícones | terça e sexta | 13:00 | 10:00 |
   | Saúde dos catálogos (amostra rotativa de 500 mídias) | semanalmente ou manual | 17:00 | 14:00 |

4. Não crie um segundo agendamento para o índice de screenshots: o pipeline WinGet já o atualiza como etapa dependente. Os catálogos Microsoft Store, Office e WinGet são publicados pelo CircleCI; evite configurar outro publicador para os mesmos destinos no GitHub Actions. O GitHub Actions continua responsável por build, release, Pages, lint, testes, backup e utilitários.

O catálogo anterior no R2 permanece disponível quando uma geração falha antes da publicação. O WinGet pode usar o último catálogo Microsoft Store válido mesmo se a atualização semanal falhar. Microsoft Store, Office e WinGet têm pipelines separados; seus resultados aparecem independentemente no CircleCI.

Após publicar catálogo e cache/API, o pipeline WinGet executa um segundo job que lista os objetos existentes em `Store/Screenshot_Database/` com paginação do R2 e gera `screenshot-index.json`, contendo apenas IDs com imagens. O app baixa esse índice compacto e o combina com `apps.json` em memória/cache local. Essa etapa não visita homepages, não baixa novamente as imagens e não atualiza milhares de registros individualmente; o número de chamadas ao R2 depende das páginas de listagem, não da quantidade de apps.

O pipeline legado `.circleci/homepage-screenshots.yml` também foi convertido para reindexar as imagens já armazenadas. `run_homepage_screenshot_sync`, `run_screenshot_sync`, `homepage_screenshot_batch_size`, `homepage_screenshot_package_id` e `homepage_screenshot_url` continuam aceitos por compatibilidade com gatilhos existentes; os parâmetros de lote/URL não são usados nessa reindexação. Não agende essa reconstrução junto com o catálogo WinGet: o próprio pipeline WinGet atualiza o índice ao concluir. Para descobrir novas imagens em páginas oficiais, esse trabalho deve permanecer separado e pontual, fora da sincronização diária do catálogo.
