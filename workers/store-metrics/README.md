# Métricas agregadas da Store

O Worker recebe um evento apenas depois de uma instalação concluída. Cada evento contém
o ID do pacote, a origem (`winget` ou `msstore`) e um UUID idempotente. Não recebe nome,
conta, identificador de máquina nem endereço de rede para armazenamento.

O Durable Object usa SQLite para deduplicar eventos e responder com contagens agregadas
dos últimos 30 dias. O workflow lê `/v1/stats` e publica o ranking diário em
`Store/Catalog/manifest/featured.json`.

## Publicar

Na pasta do projeto:

```bash
npx wrangler deploy --config workers/store-metrics/wrangler.toml
```

O primeiro deploy cria a classe SQLite `InstallMetrics`. O endpoint esperado pelo app
e pelo workflow é:

```text
https://winprovision-store-metrics.gabriel-silva20090.workers.dev
```

Depois da publicação, confirme `GET /health` e `GET /v1/stats`. Se a conta Cloudflare
usar outro subdomínio `workers.dev`, ajuste a URL em `InstallMetricsService.cs` e em
`catalogs.yml`.
