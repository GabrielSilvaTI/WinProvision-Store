# Migração dos ícones legados para o catálogo V2

O script `scripts/database/migrate_legacy_icons.py` transfere ícones do manifesto legado (`Store/icon-manifest.json` e objetos em `Store/Icon_Database/`) para `media/icon.png` junto ao JSON de cada app no catálogo V2.

O padrão é somente auditoria. A publicação exige `--apply`. O migrador cruza IDs sem diferenciar maiúsculas/minúsculas, exige que a URL antiga aponte para um objeto existente no prefixo legado e preserva qualquer ícone que já esteja associado na V2. Não remove nem altera os arquivos antigos. As publicações são sequenciais em lotes, com checkpoint em `media-index.json`, índice de busca e manifesto; o manifesto é atualizado por último.

## Executar no WSL

Na raiz do repositório:

```bash
cd /mnt/c/Gemini
source .venv/bin/activate
```

Defina as credenciais R2 na sessão atual do terminal. Não as grave no repositório:

```bash
export R2_ACCOUNT_ID='ACCOUNT_ID_DO_CLOUDFLARE'
export R2_ACCESS_KEY_ID='ACCESS_KEY_ID_DO_TOKEN'
read -rsp 'Secret Access Key do mesmo token: ' R2_SECRET_ACCESS_KEY; echo
export R2_SECRET_ACCESS_KEY
```

Faça primeiro a auditoria sem publicar:

```bash
python scripts/database/migrate_legacy_icons.py
```

O relatório `legacy-icon-migration-report.json` resume quantos apps já têm ícone V2, quantos têm correspondência antiga utilizável e quantos estão sem ícone, com ID ambíguo, URL inválida ou objeto antigo ausente. Só os IDs exatos (ignorando maiúsculas/minúsculas) são elegíveis.

Depois de revisar o relatório, publique todos os candidatos em checkpoints de 100 apps:

```bash
python scripts/database/migrate_legacy_icons.py --apply --batch-size 100
```

Para uma validação inicial limitada a 100 apps:

```bash
python scripts/database/migrate_legacy_icons.py --apply --limit 100 --batch-size 100
```

O script atualiza `app.json`, `media-index.json`, `search-index.json` e seus hashes. Ao terminar e validar a cobertura na V2, os objetos e processos legados podem ser retirados em uma etapa separada.

## Migrar screenshots antigas

`scripts/database/migrate_legacy_screenshots.py` usa o índice antigo `Store/Database/screenshot-index.json` para copiar as capturas WinGet e Homepage que ainda existem em `Store/Screenshot_Database/`. O padrão continua sendo auditoria sem escrita:

```bash
python scripts/database/migrate_legacy_screenshots.py
```

Revise `legacy-screenshot-migration-report.json`. O migrador copia todas as capturas listadas no índice antigo, incluindo as fontes WinGet e Homepage. As imagens V2 já associadas são preservadas e não impedem a cópia das antigas. A janela de detalhes atualmente exibe até 12 screenshots por app, mas as demais ficam organizadas e associadas na V2 para uso futuro. Os nomes V2 são determinísticos e normalizados, por exemplo `01-legacy-winget-<hash>.png` e `02-legacy-homepage-<hash>.webp`.

Para testar um pequeno lote antes de migrar todas as elegíveis:

```bash
python scripts/database/migrate_legacy_screenshots.py --apply --limit 50 --batch-size 50
```

Depois de conferir o resultado, execute sem `--limit` para migrar todas as demais:

```bash
python scripts/database/migrate_legacy_screenshots.py --apply --batch-size 100
```

Os objetos antigos não são apagados. O migrador valida cada imagem, evita associar duas vezes o mesmo conteúdo e publica os checkpoints com o manifesto por último.
