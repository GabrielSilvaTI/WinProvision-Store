# Pipelines de catálogo e mídia

Os trabalhos de publicação no R2 têm uma área por função. Cada workflow do CircleCI roda sem `requires` de outro workflow: uma falha no Office, nos ícones ou no WinGet não cancela os demais. O arquivo já usado pelo projeto continua em `.circleci/config.yml`.

| Função | Configuração | Parâmetro manual | Destino |
| --- | --- | --- | --- |
| Catálogo Microsoft Store | `.circleci/config.yml` | `run_msstore_catalog=true` | `Store/Database/msstore-catalog.json` e assets |
| Assets e ofertas Office | `.circleci/office.yml` | `run_office_assets=true` | `Office/Database/` e assets |
| Catálogo WinGet e API | `.circleci/winget-sync.yml` | `run_winget_catalog=true` | `Store/Database/apps.json` e `Store/Api/v1/` |
| Screenshots curados do WinGet | `.circleci/screenshots.yml` | `run_screenshot_sync=true` | `Store/Screenshot_Database/` e `screenshotUrls` |
| Ícones da CDN WinGet | `.circleci/icons.yml` | `run_icon_sync=true`; `publish_icons=true` para publicar | `Store/Icon_Database/` |
| Manifesto de ícones | `.circleci/icon-manifest.yml` | `run_icon_manifest=true` | `Store/icon-manifest.json` |
| Screenshots das páginas oficiais | `.circleci/homepage-screenshots.yml` | `run_homepage_screenshot_sync=true`; `homepage_screenshot_batch_size` opcional (padrão 100) | `Store/Screenshot_Database/Homepage/` e campos de screenshot do catálogo |

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
   | Screenshots das páginas oficiais | diariamente | 15:00 | 12:00 |

4. **Depois que os novos gatilhos e uma execução real de cada publicação estiverem confirmados**, retirar os agendamentos e gatilhos `push` duplicados de `.github/workflows/update-msstore-catalog.yml` e `.github/workflows/winget-sync.yml`, e o agendamento de `.github/workflows/sync-icon-manifest.yml`. Manter `workflow_dispatch` como recuperação manual. Isso evita duas publicações simultâneas dos mesmos objetos no R2. O GitHub Actions continua responsável por build Windows, release, Pages, lint, testes Windows, backup e utilitários.

O catálogo anterior no R2 permanece disponível quando uma geração falha antes da publicação. O WinGet pode usar o último catálogo Microsoft Store válido mesmo se a atualização semanal falhar. Microsoft Store, Office e WinGet têm pipelines separados; seus resultados aparecem independentemente no CircleCI.

O crawler de screenshots roda em lote para limitar tráfego nos sites dos mantenedores. Ele só acessa páginas HTTPS públicas, respeita `robots.txt`, ignora pacotes que já têm screenshots e procura imagens sinalizadas como captura de tela. Sites que montam a página somente com JavaScript ou não identificam as capturas no HTML serão ignorados; falhas de um site são registradas no artifact e não cancelam os demais. Para ativar, adicione `.circleci/homepage-screenshots.yml` como pipeline independente e configure um schedule diário na `main` com `run_homepage_screenshot_sync=true`. O estado e o cursor ficam em `Store/Database/homepage-screenshot-state.json`, para que cada execução continue do ponto anterior.
