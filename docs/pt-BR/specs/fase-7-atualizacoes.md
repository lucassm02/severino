# Spec: Fase 7, Aviso de versão nova

Status: concluída em 2026-10-10. Critérios de pronto verificados com o [guia de testes](../testes/fase-7-atualizacoes.md); a spec foi aprovada no mesmo dia, com os padrões das "Decisões a confirmar".

## Objetivo

Hoje quem instalou o Severino só sabe que saiu versão nova se voltar ao site ou ao GitHub. Nesta fase, o app avisa sozinho e atualiza com um clique:

- Ele consulta a última release no GitHub ao abrir e uma vez por dia enquanto fica aberto.
- Se houver versão nova, mostra um aviso no topo da janela e uma notificação na bandeja, uma vez por versão.
- **Atualizar agora** baixa o instalador e confere o SHA-256. Depois fecha o app, instala em silêncio e abre o app de novo. Rotas, serviços e configurações ficam como estavam.

## Critérios de pronto

1. **Avisa.** Com uma versão mais antiga instalada, o aviso aparece no topo da janela em até um minuto depois de abrir. Ele diz qual versão saiu e qual está instalada.
2. **Notifica uma vez.** Com a janela escondida, a bandeja mostra uma notificação da versão nova, e não repete a mesma versão.
3. **Atualiza.**
   - **Atualizar agora** baixa o instalador da release, mostra o progresso e confere o SHA-256 informado pelo GitHub.
   - Depois de confirmado, o app fecha e o instalador roda sem telas, só com o UAC. Ao fim, o app abre de novo, na versão nova.
4. **Não instala o que não confere.**
   - Se o hash não bater, o download é apagado e nada roda. O mesmo vale se o download falhar ou se a release não informar o hash.
   - O aviso explica o que houve e oferece abrir a página da release.
5. **Respeita a escolha.**
   - **Pular esta versão** some com o aviso até sair uma versão mais nova que essa.
   - Fechar o aviso o esconde até a próxima vez que o app abrir.
   - Desligar **Procurar versões novas** nas Configurações para as consultas automáticas.
6. **Sobre.** Configurações › Sobre mostra a versão instalada e o resultado da última consulta, com **Procurar agora**. Se houver versão nova, mostra também **Atualizar agora**.
7. **Silencioso quando falha.** Sem internet ou com o GitHub fora do ar, a consulta automática não mostra erro nenhum. Só **Procurar agora** diz que não conseguiu.

## Como funciona

**Consulta.**
- É um `GET https://api.github.com/repos/lucassm02/severino/releases/latest`, sem login, com `User-Agent: Severino/<versão>`.
- A resposta já vem sem pré-releases nem rascunhos.
- Da resposta saem:
  - a tag (`v0.5.0`);
  - a página da release;
  - o `.exe` anexado, com a URL, o tamanho e o `digest` (`sha256:…`).
- A versão da tag é comparada com a do app, com três números.
- A consulta automática roda 30 s depois de abrir e depois a cada hora, mas só vai à rede se a última consulta tiver mais de 24 h.
- O limite da API sem login (60 por hora por IP) fica longe.

**O que fica salvo** no `config.json`:
- `Settings.CheckForUpdates` (ligado por padrão);
- `State.LastUpdateCheck`;
- `State.SkippedVersion`;
- `State.NotifiedVersion`.

**Download.**
- Vai para `%TEMP%\Severino\Severino-Setup-<versão>.exe`, com progresso.
- O SHA-256 é calculado durante o download e comparado com o `digest`.
- Um arquivo que não confere é apagado na hora.

**Instalação.**
1. Antes, uma confirmação: "O Severino fecha, instala a versão 0.5.0 e abre de novo. O Windows vai pedir permissão de administrador."
2. O app para o proxy, como em qualquer saída.
3. Roda o instalador com `/SILENT /SP- /NORESTART /RELAUNCH=1` e fecha.
4. O instalador:
   - pede o UAC;
   - instala por cima, com o mesmo `AppId`;
   - com `/RELAUNCH=1`, abre o app de novo como o usuário que o chamou (entrada nova no `[Run]`, só em modo silencioso).

Se o UAC for recusado, nada é instalado e o app fica fechado; basta abrir de novo. Se o app ainda estiver fechando quando o instalador começar, o próprio instalador pede para fechá-lo (`AppMutex`).

**Em desenvolvimento** (build Debug), a consulta automática fica desligada; **Procurar agora** continua funcionando.

**Privacidade.**
- A consulta só leva o IP e o `User-Agent` com a versão. Não manda identificador nem dado de uso.
- Isso fica escrito no README e nas Configurações, junto da opção de desligar.

## Decisões a confirmar

1. **Atualizar dentro do app**, baixando e rodando o instalador, em vez de só abrir a página da release. *Padrão: sim*, com a conferência do SHA-256. A página da release continua a um clique, em **Ver novidades**.
2. **Consulta automática ligada por padrão.** *Padrão: sim*, uma vez por dia, com opção de desligar.
3. **Release sem `digest` não instala pelo app.** *Padrão: sim.* O aviso aparece do mesmo jeito, e o botão leva à página da release.
4. **Pré-releases ficam de fora.** *Padrão: sim.* O endpoint `latest` já as ignora.

## Fora do escopo

- Atualização em segundo plano sem pedir, ou instalar sozinho ao fechar.
- Assinatura do instalador (segue a Fase 5) e verificação de assinatura.
- Canal beta.
- Mostrar o changelog dentro do app. **Ver novidades** abre a release no navegador.

## Etapas

1. Core: `UpdateChecker` (consulta e comparação de versões) e `UpdateDownloader` (download com SHA-256), com testes usando um `HttpMessageHandler` falso.
2. Config: os campos novos.
3. App: `UpdateService` (agenda, estado, instalar), o aviso no topo da janela, a notificação na bandeja e o bloco **Sobre** nas Configurações.
4. Instalador: `/RELAUNCH=1` no `[Run]`.
5. README e CHANGELOG.
6. Roteiro manual em `docs/testes/fase-7-atualizacoes.md`: instalar uma versão antiga e atualizar pelo app.
