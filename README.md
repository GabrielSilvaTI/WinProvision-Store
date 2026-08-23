# WinProvision Store

Catálogo curado de aplicativos do WinGet, no estilo [Winstall](https://winstall.app) / [UniGetUI](https://github.com/marticliment/UniGetUI) — com filtragem de ruído, score de relevância e destaque para apps regionais.

## Como funciona

```
[winget-pkgs (github)] → [WinProvision.Indexer] → [branch "database"] → [app cliente]
```

Todos os dias às 03:00 UTC (ou manualmente via `workflow_dispatch`), o workflow `db-sync.yml`:

1. Clona o [`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs) (shallow clone).
2. Executa o `WinProvision.Indexer`, que varre os manifests YAML, filtra, pontua e exporta o catálogo.
3. Publica os arquivos JSON resultantes na branch `database` (isolada da `main`, nunca gera conflito).

## Pipeline do Indexer (`WinProvision.Indexer`)

| Etapa | O que faz |
|---|---|
| **1. Varredura + dedup** | Lê todos os manifests e mantém **apenas a versão mais recente** de cada `PackageIdentifier`. O winget-pkgs guarda todo o histórico de versões publicadas, então sem esse corte a contagem final fica bem maior que o número real de apps distintos. |
| **2. Filtro de ruído** | Descarta fontes, redistribuíveis de runtime (`VCRedist`, `.NET`, `VCLibs`), pacotes de idioma isolados e manifests incompletos. Regras em [`config/noise-rules.json`](src/WinProvision.Indexer/config/noise-rules.json) — editável sem recompilar. |
| **3. Classificação regional** | Marca apps com apelo regional (hoje: Brasil) via domínio do site (`.com.br`, `.gov.br`), publishers conhecidos e palavras-chave (Pix, NFe, Receita Federal...). Estrutura extensível a outras regiões. |
| **4. Enriquecimento GitHub** | Para apps com repositório GitHub identificável na Homepage/PackageUrl, busca estrelas, forks e data do último push via API REST, com cache em disco (`metrics-cache.json`, TTL 7 dias) e `GITHUB_TOKEN` nativo do Actions (5.000 req/h). |
| **5. Score (0–100)** | `Completude × 0.35 + Popularidade × 0.35 + Manutenção × 0.30` — pesos em [`config/scoring-weights.json`](src/WinProvision.Indexer/config/scoring-weights.json). Apps **sem** repositório GitHub (a maioria dos apps proprietários mais usados — Chrome, Spotify, Zoom...) recebem um score neutro nos componentes de Popularidade/Manutenção em vez de serem penalizados. |
| **6. Exportação segmentada** | Gera 4 arquivos JSON (ver abaixo) para o cliente nunca precisar baixar a base inteira. |

## Arquivos publicados na branch `database`

| Arquivo | Conteúdo | Uso sugerido |
|---|---|---|
| `apps.json` | Catálogo completo, higienizado e pontuado | Download sob demanda / tela de detalhes |
| `apps-featured.json` | Top 500 por score | Destaques da home |
| `apps-regional-br.json` | Apenas apps com `regionTags` contendo `BR` | Seção "Apps do Brasil" |
| `apps-search-index.json` | Só `id`, `name`, `publisher`, `score`, `tags` | Autocomplete / busca instantânea |
| `metrics-cache.json` | Cache interno de métricas do GitHub | Uso interno da pipeline (não é para o app cliente) |

## Estrutura do repositório

```
src/
  WinProvision.Core/           # Modelos + serviços compartilhados
    Models/                    # AppEntry, GitHubRepoMetrics, ScoringWeights
    Services/
      Indexing/                # Parser YAML, scanner, filtros, score, exportação
      StoreService.cs          # Consome o catálogo publicado (usado pelo app cliente)
      IconService.cs           # Resolução de ícones (favicon / CDN)
      WingetExecutor.cs        # Instalação/desinstalação via winget.exe
  WinProvision.Indexer/        # Ponto de entrada da pipeline de indexação (CI)
    config/                    # scoring-weights.json, noise-rules.json
  WinProvision.ConsoleDemo/    # Cliente de teste manual (busca + instalação via console)
```

> A interface gráfica (WPF/MAUI/outro) ainda não existe neste repositório — os serviços em `WinProvision.Core` (`StoreService`, `IconService`, `WingetExecutor`) já estão prontos para serem consumidos por ela quando for criada.

## Rodando localmente

```bash
# Clonar uma amostra do winget-pkgs (ou usar o clone completo)
git clone --depth 1 https://github.com/microsoft/winget-pkgs.git

# Publicar e rodar o indexer
dotnet publish src/WinProvision.Indexer -c Release -o ./publish
dotnet ./publish/WinProvision.Indexer.dll ./winget-pkgs/manifests ./output

# (opcional) testar busca/instalação interativamente
dotnet run --project src/WinProvision.ConsoleDemo
```

Sem `GITHUB_TOKEN` no ambiente, o enriquecimento via GitHub roda no limite não autenticado (60 req/h) e pode atingir rate limit rapidamente numa base de milhares de apps — normal em teste local, não é erro.
