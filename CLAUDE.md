# Severino

App desktop Windows para apontar domínios para apps locais de dev (`callfred.sev → 127.0.0.1:3000`): grava o hosts, roteia por um proxy reverso em loopback e, opcionalmente, emite HTTPS confiável por uma CA local.

**Fonte da verdade do design:** [docs/planejamento.md](docs/planejamento.md). Leia antes de propor arquitetura, stack ou escopo. Divergências do plano devem ser discutidas e registradas lá.

## Decisões fixas

- .NET 10, WPF + WPF-UI, CommunityToolkit.Mvvm, YARP/Kestrel no mesmo processo da UI.
- Dois processos: `Severino.App` roda como usuário comum; `Severino.Helper` (serviço Windows) é o único elevado e só sincroniza o bloco do hosts + flush de DNS, via named pipe.
- `Severino.Contracts` contém o validador de domínio compartilhado; o Helper não depende de mais nada e revalida tudo (sintaxe do nome, limite de entradas, IP sempre loopback).
- Domínio livre, sem lista de sufixos; pode sobrescrever um domínio real. O app só avisa (DNS consultado sem o hosts, HSTS preload, `.local`). Os exemplos usam `.sev`.
- Proxy escuta só em `127.0.0.1` e `[::1]`. HTTP→HTTPS com 307, nunca 301, nunca HSTS.
- CA ECDSA P-256 com Name Constraints nos domínios cadastrados (TLD inteiro quando o TLD não existe na internet; nome exato quando existe), chave via DPAPI, instalada em `CurrentUser\Root`. Domínio fora da cobertura exige reemitir a CA.
- Fora do escopo: expor na rede/internet, Let's Encrypt, load balancing, containers/WSL.

## Comandos

```bash
dotnet build
dotnet test
dotnet run --project src/Severino.App
```

## Convenções

- Commits: Conventional Commits em inglês.
- Versões de pacotes ficam em `Directory.Packages.props`; `Directory.Build.props` liga `TreatWarningsAsErrors`.
- Modelos de config usam `{ get; set; }`, não `init` (ver comentário em `SeverinoConfig.cs`); trate as instâncias como imutáveis e altere com `with` via `ConfigService.Update`.

## Estado

Fase 0 concluída. Fase 1 implementada conforme [docs/specs/fase-1-mvp-http.md](docs/specs/fase-1-mvp-http.md); falta o checklist manual com o Helper instalado (`scripts/dev-helper.ps1 install`, em terminal elevado): fluxo com hosts, HMR do Vite e limpeza do bloco ao sair.
