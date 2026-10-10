# Spec: Fase 8, Distribuição pelo winget

Status: rascunho, aguardando aprovação. Nada implementado.

## Objetivo

Quem desenvolve no Windows instala e atualiza o Severino pelo terminal, sem abrir o site:

```powershell
winget install lucassm02.Severino
winget upgrade lucassm02.Severino
```

Cada release publicada pelo workflow **Release** chega ao winget sozinha, sem ninguém editar manifesto à mão.

## Critérios de pronto

1. **No catálogo.** `winget search severino` acha o pacote `lucassm02.Severino`, e `winget show lucassm02.Severino` mostra a versão mais recente, a licença MIT e os links do site e do repositório.
2. **Instala sem perguntar nada.** `winget install lucassm02.Severino` termina sem nenhuma janela além do UAC: app instalado, serviço auxiliar registrado e rodando, módulo PowerShell no lugar. O app não abre sozinho.
3. **Atualiza por cima.** `winget upgrade` instala a versão nova sobre a antiga, com o app aberto ou fechado, e mantém rotas, serviços, entradas DNS e configurações.
4. **Desinstala sem travar.** `winget uninstall lucassm02.Severino` termina sem esperar resposta: tira o serviço, os blocos do hosts e as regras de DNS. O que exige confirmação do Windows (a CA) segue a decisão 3.
5. **Release chega sozinha.** Depois do primeiro pacote aprovado, rodar o **Release** abre o PR da versão nova no `microsoft/winget-pkgs`, com URL e SHA-256 do instalador.
6. **Site e README** mostram o comando do winget ao lado do botão de download.

## O pacote

Manifesto em três arquivos (versão, instalador e textos), no formato atual do `winget-pkgs`:

- **PackageIdentifier:** `lucassm02.Severino`. **Moniker:** `severino`.
- **Publisher:** `lucassm02`. O instalador hoje grava `AppPublisher=Severino` no Painel de Controle; o manifesto declara isso em `AppsAndFeaturesEntries`, ou o instalador passa a gravar `lucassm02` (decisão 4).
- **License:** MIT, com `LicenseUrl` para o `LICENSE` do repositório.
- **Textos:** idioma padrão `en-US` (descrição curta e longa em inglês), com um manifesto extra `pt-BR` com os textos do site. O catálogo é lido no mundo todo, e a busca casa melhor em inglês.
- **Tags:** `hosts`, `dns`, `https`, `reverse-proxy`, `local-development`, `wsl`, `docker`, `kubernetes`.
- **Instalador:**
  - `InstallerType: inno`. O winget já passa `/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` para instalar em silêncio.
  - `Architecture: x64`, `Scope: machine`, `ElevationRequirement: elevatesSelf` (o instalador pede o UAC sozinho).
  - `ProductCode: {6F3B1C2A-8D4E-4F5A-9B7C-2E1D0A9F8B76}_is1`, o `AppId` do Inno, para o winget reconhecer o que já está instalado e saber atualizar.
  - `UpgradeBehavior: install`: a versão nova instala por cima, como já acontece hoje.
  - `InstallerUrl`: o `.exe` anexado à release no GitHub, com `InstallerSha256` calculado na hora.
  - `MinimumOSVersion: 10.0.17763`, o mesmo do instalador.

Os manifestos ficam versionados em `packaging/winget/`, gerados por script a partir da versão, da URL e do hash, para dar para revisar o que vai para o catálogo.

## O instalador em modo silencioso

O que já funciona:

- O "Abrir o Severino" do fim tem `skipifsilent`: em silêncio, o app não abre.
- As perguntas da desinstalação usam `SuppressibleMsgBox`. Em silêncio, ficam com a resposta padrão: desinstalar mesmo instalado por outro usuário, e manter as rotas e configurações.
- O usuário do serviço auxiliar vem de `ExecAsOriginalUser`, que no winget é quem rodou o comando, mesmo com o UAC aprovado por outra conta.

O que precisa mudar ou ser validado:

1. **A CA na desinstalação.** O `Severino.exe --cleanup` remove a CA de `CurrentUser\Root`, e o Windows sempre pede confirmação para isso. Em silêncio, essa janela trava o `winget uninstall` (ver decisão 3).
2. **App aberto durante o update.** O instalador tem `AppMutex` e `CloseApplications`. Falta testar o que o Inno faz em `/VERYSILENT /SUPPRESSMSGBOXES` com o Severino aberto: se fecha o app sozinho, se aborta, ou se espera. O critério 3 pede que funcione com o app aberto, de preferência fechando e abrindo de novo na bandeja.
3. **Validação da Microsoft.** Os PRs do `winget-pkgs` instalam o pacote numa máquina limpa e rodam o Defender. O instalador não é assinado (decisão da Fase 5), o que o catálogo aceita, mas pode atrasar a análise. Testar antes, localmente, com `winget install --manifest packaging/winget/<versão>` e `winget validate`.

## Automação no Release

Depois do passo que cria a release, um passo novo no `release.yml`:

1. Baixa o `wingetcreate` (ferramenta da Microsoft para manifestos).
2. Roda `wingetcreate update lucassm02.Severino --version X.Y.Z --urls <url do .exe> --submit`, que gera os manifestos da versão nova a partir dos anteriores e abre o PR no `microsoft/winget-pkgs` pela conta do dono.
3. Usa um token guardado no secret `WINGET_TOKEN`. Sem o secret, o passo é pulado com um aviso, e a release continua valendo.

A primeira versão entra à mão: os manifestos de `packaging/winget/` são enviados com `wingetcreate submit`, e o PR passa pela revisão de um moderador. Só depois disso o `update` automático funciona.

## Configuração que só o dono do repositório faz

- Um fork de `microsoft/winget-pkgs` na conta `lucassm02` (o `wingetcreate` cria se não existir).
- Um token do GitHub com permissão `public_repo`, salvo como secret `WINGET_TOKEN` no repositório.
- Acompanhar o primeiro PR no `winget-pkgs` e responder aos moderadores, se pedirem algo.

## Decisões a confirmar

1. **Identificador `lucassm02.Severino`.** *Padrão: sim.* O formato é `Publisher.Nome`; um nome sem o usuário (`Severino.Severino`) passaria a impressão de uma empresa.
2. **Manifesto padrão em inglês, com pt-BR como extra.** *Padrão: sim.*
3. **CA na desinstalação silenciosa.** *Padrão: em silêncio, o desinstalador não remove a CA* e deixa um aviso no log. A chave privada da CA fica protegida por DPAPI na pasta de dados do usuário, e "Limpar tudo" no app continua removendo tudo. Alternativa: remover em silêncio usando outra API, se existir uma que não peça confirmação (a validar), ou manter a janela e aceitar que o `winget uninstall` espere por ela.
4. **Publisher no Painel de Controle passa a ser `lucassm02`.** *Padrão: sim*, para o winget e o Windows mostrarem o mesmo nome.
5. **Envio automático a cada release.** *Padrão: sim*, depois da primeira aprovação. Quem não quiser uma versão no winget roda o Release sem o secret.

## Fora do escopo

- Scoop e Chocolatey. Podem vir depois, com a mesma ideia de manifesto gerado no Release.
- Microsoft Store.
- Assinatura do instalador (segue a decisão da Fase 5).
- Atualização automática dentro do app; o `winget upgrade` cobre quem instalou pelo winget.

## Etapas

1. Instalador: comportamento silencioso da CA (decisão 3), Publisher (decisão 4) e teste do update com o app aberto.
2. `packaging/winget/` e o script que gera os manifestos; `winget validate` e instalação local pelo manifesto.
3. Envio manual da primeira versão e acompanhamento do PR.
4. Passo do `wingetcreate update` no `release.yml`.
5. Comando do winget no site e no README, depois da aprovação.
6. Roteiro de teste manual em `docs/testes/fase-8-winget.md`: instalar, atualizar com o app aberto, desinstalar, tudo pelo winget.
