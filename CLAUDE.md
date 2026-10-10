# Severino

App desktop Windows para apontar domínios para apps locais de dev (`callfred.sev → 127.0.0.1:3000`): grava o hosts, roteia por um proxy reverso em loopback e, opcionalmente, emite HTTPS confiável por uma CA local.

**Fonte da verdade do design:** [docs/planejamento.md](docs/planejamento.md). Leia antes de propor arquitetura, stack ou escopo. Divergências do plano devem ser discutidas e registradas lá.

## Decisões fixas

- .NET 10, WPF + WPF-UI, CommunityToolkit.Mvvm, YARP/Kestrel no mesmo processo da UI.
- Dois processos: `Severino.App` roda como usuário comum; `Severino.Helper` (serviço Windows) é o único elevado e só sincroniza o bloco do hosts + flush de DNS, via named pipe.
- `Severino.Contracts` contém o validador de domínio compartilhado; o Helper não depende de mais nada e revalida tudo (sintaxe do nome, limite de entradas, endereços). O bloco das rotas e serviços só aceita loopback. O bloco DNS da Fase 6 aceita loopback e faixas privadas; IP público só depois de aprovado por UAC ([spec](docs/specs/fase-6-dns-e-extras.md)).
- Domínio livre, sem lista de sufixos; pode sobrescrever um domínio real. O app só avisa (DNS consultado sem o hosts, HSTS preload, `.local`). Os exemplos usam `.sev`.
- Proxy escuta só em `127.0.0.1` e `[::1]`. HTTP→HTTPS com 307, nunca 301, nunca HSTS.
- CA ECDSA P-256 com Name Constraints nos domínios cadastrados (TLD inteiro quando o TLD não existe na internet; nome exato quando existe), chave via DPAPI, instalada em `CurrentUser\Root`. Domínio fora da cobertura exige reemitir a CA.
- Fora do escopo: expor na rede/internet, Let's Encrypt, load balancing, resolução de nomes dentro de containers. Kubernetes, Docker e WSL entram na Fase 4 como fontes de serviços e como chamadores ([spec](docs/specs/fase-4-servicos.md)), sem o Helper deixar de gravar só loopback.

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
- Ícones: `src/Severino.App/Assets/severino.ico` e `severino-512.png` são gerados por `dotnet run scripts/build-icons.cs` a partir de `assets/branding/severino-rosto.png`. Não edite os gerados à mão.
- Cores de texto do WPF-UI: use `Foreground="{DynamicResource TextFillColorSecondaryBrush}"`, não `Appearance="Secondary"`, que fixa a cor na criação e quebra a troca de tema com o app aberto. A cor de destaque é a da marca (`Services/Brand.cs`), aplicada antes de trocar o tema.

## Estado

Fases 0 a 3 concluídas e verificadas (specs em [docs/specs/](docs/specs/); roteiros manuais em [docs/testes/](docs/testes/)). Em desenvolvimento: Fase 4, Serviços: Kubernetes, Docker e WSL ([spec](docs/specs/fase-4-servicos.md)), falta o checklist manual; e Fase 5, site e publicação ([spec](docs/specs/fase-5-site.md)): o site fica em `site/` (prévia com `python -m http.server 8765 --directory site`), e as releases saem do workflow Release. Em desenvolvimento, o Helper se instala com `scripts/dev-helper.ps1 install` num terminal elevado; o instalador sai de `scripts/build-installer.ps1`.

Ao chamar algo que abre um aviso do próprio Windows (instalar ou remover CA), use `WindowsPrompt.Run`: o aviso nasce sem janela dona e cairia atrás do app.
