# Workflows de CI/CD

O GitHub Actions é o único executor automatizado do repositório. Os workflows do CircleCI foram removidos; não configure novos pipelines nele. Todos os publicadores de catálogo usam concorrência para impedir escritas simultâneas no R2.

| Workflow | Execução | Função |
| --- | --- | --- |
| `.github/workflows/ci.yml` | Push e pull request em `main`, ou manual | Ruff, formatação de C#/XAML, validação dos workflows com actionlint, compilação do Indexer e do app WPF, e testes unitários. |
| `.github/workflows/catalogs.yml` | Agendado ou manual | Sincronização WinGet, Microsoft Store, gestão de mídia, sondagem de ícones e verificação dos catálogos. |
| `.github/workflows/integration.yml` | Pull requests relevantes, semanal ou manual | Integração com WinGet e smoke test do atualizador Windows. |
| `.github/workflows/security.yml` | Push/PR em `main`, semanal ou manual | CodeQL para C#, JavaScript/TypeScript e Python; revisão de dependências novas nos pull requests. |
| `.github/workflows/release.yml` | Tag `v*`, pull request ou manual | Compila o cliente, testa o instalador e publica versões estáveis, pré-lançamentos ou nightly. |
| `.github/workflows/format-xaml.yml` | Manual | Formata XAML em uma branch e abre um pull request para revisão. |
| `.github/workflows/pages.yml` | Manual | Publica `docs/` no GitHub Pages. |

## Catálogos e mídia

`catalogs.yml` tem as operações `winget`, `msstore`, `featured`, `media`, `health` e `icon_probe`. Na aba **Actions**, escolha **Catálogos e mídia** e selecione **Run workflow** para iniciar uma delas. Operações que publicam pedem os segredos de R2 somente dentro do job correspondente. Saúde e sondagem são somente leitura.

Agendamentos em UTC:

| Operação | Agenda | Horário em Brasília (UTC−3) |
| --- | --- | --- |
| WinGet | Diariamente, 08:00 | 05:00 |
| Microsoft Store | Domingo, 03:00 | 00:00 |
| Ícones WinGet | Terça e sexta, 11:00 | 08:00 |
| Destaques | Diariamente, 12:00 | 09:00 |
| Saúde dos catálogos | Domingo, 17:00 | 14:00 |

Na execução manual de mídia, `package_id` identifica o app; `asset_type` seleciona ícone ou screenshot. Para screenshots em lote, informe `media_urls` com URLs separadas por espaços ou quebras de linha, ou como array JSON. No modo automático, `scan_prefix` aceita `0-9` ou uma letra; em branco continua do cursor salvo. `batch_size` limita o número analisado naquela execução.

WinGet e Microsoft Store publicam seus catálogos V2 em `Store/Catalog/` e `Store/Catalog/msstore/`. A API própria consome esses mesmos detalhes e índices; não há um catálogo/API duplicado em `Store/Api/`. O cache operacional do Indexer continua em `Store/Database/metrics-cache.json`.

O job WinGet verifica o fingerprint publicado e ignora a reconstrução quando a mesma revisão de origem e de build já foi validada. Em caso de alteração, baixa apenas os manifests do WinGet, restaura os índices e caches do R2, executa o Indexer C#, constrói e valida o catálogo em Python e publica os dados antes do manifesto. O lock `catalog-root-writer` serializa publicações que compartilham o prefixo raiz.

O job `featured` combina os índices V2 WinGet e Microsoft Store e publica `Store/Catalog/manifest/featured.json`. O Worker em `workers/store-metrics/` recebe IDs de pacotes e origem somente após instalações concluídas; mantém eventos por até 90 dias e entrega agregados móveis de 30 dias. O app usa o catálogo editorial em cache e mostra a contagem observada quando disponível. Publique o Worker uma vez com `npx wrangler deploy --config workers/store-metrics/wrangler.toml`; até lá, a curadoria usa avaliações e sinais de qualidade do catálogo.

## Segredos de repositório

Configure em **Settings > Secrets and variables > Actions**:

- `R2_ACCOUNT_ID`
- `R2_ACCESS_KEY_ID`
- `R2_SECRET_ACCESS_KEY`
- `R2_BUCKET_NAME` (opcional; padrão `winprovision`; `R2_BUCKET` também é aceito)

O token `GITHUB_TOKEN` é fornecido pelo próprio Actions. Não coloque credenciais nos arquivos YAML ou nos parâmetros manuais.

## CircleCI

Os arquivos de configuração CircleCI não fazem mais parte deste repositório. Se pipelines ainda estiverem cadastrados no painel do CircleCI, desative-os lá depois que a nova configuração estiver disponível em `main`; apagar os arquivos do repositório não altera configurações armazenadas no serviço.
