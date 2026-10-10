# Guia de testes: Fase 2, HTTPS

Roteiro manual para fechar a Fase 2. Cobre os critérios de pronto do [spec](../specs/fase-2-https.md) e o checklist manual dele, mais o que mudou depois: o diálogo antes do aviso do Windows e as páginas de erro com a marca.

Cada teste diz o que fazer, o que tem de acontecer e qual critério ele prova. Anote o resultado na tabela do fim. Os testes seguem uma ordem pensada: o teste 1 parte da CA ativa e a remove, e os seguintes reconstroem tudo do zero. Leva uns 40 minutos.

## Convenções

- Os comandos são para **PowerShell**, num terminal comum, sem administrador, a não ser quando o passo disser o contrário.
- `curl.exe` (com `.exe`, para não cair no apelido do PowerShell) usa o Schannel, o validador do Windows. Ele exige verificação de revogação, e a CA local não publica lista de revogação. Por isso todo `curl.exe` com `https` leva `--ssl-no-revoke`. Os navegadores não exigem isso.
- "Rota de teste" é qualquer rota com um servidor de dev respondendo. Os exemplos usam `meuapp.sev`; troque pela sua.
- Ao fechar o Severino, use **Sair** no ícone da bandeja. Fechar a janela só esconde o app.

## 0. Preparação

1. **Testes automatizados e build** (critério 10). Na pasta do projeto:

   ```powershell
   dotnet build
   dotnet test
   ```

   Esperado: build com `0 Aviso(s)` e `0 Erro(s)`, e todos os testes aprovados. Os testes de `WindowsDnsResolverTests` consultam o DNS de verdade. Se falharem por tempo esgotado, rode de novo antes de concluir algo.

2. **Helper rodando:**

   ```powershell
   Get-Service Severino.Helper
   ```

   Esperado: `Running`. Se não estiver, rode `./scripts/dev-helper.ps1 install` num terminal como administrador.

3. **Servidor de dev com HMR.** O teste 4 precisa de um app Vite. Se a sua rota de teste não for Vite, crie um descartável:

   ```powershell
   npm create vite@latest severino-hmr -- --template vanilla
   cd severino-hmr; npm install; npm run dev -- --port 5173
   ```

   Depois, no Severino, crie a rota `hmr.sev → http://localhost:5173`.

4. **Abra o Severino:**

   ```powershell
   dotnet run --project src/Severino.App
   ```

   Esperado na barra inferior: `Proxy ativo :80 :443 · HTTPS ok · hosts ok`.

## 1. Remover CA

Prova o critério 9, na parte da remoção, e o item "Remover CA e Ativar de novo" do checklist.

1. Configurações › HTTPS › **Remover CA**.
2. Confira o diálogo do app: título "Remover CA", texto dizendo que o Windows pede confirmação, botões "Remover" e "Cancelar". Clique **Remover**.
3. O Windows mostra uma confirmação para excluir o certificado da raiz. Clique **Sim**.

**Esperado:**

- O cartão mostra "Desativado. As rotas abrem só por http://." e a mensagem "CA removida.".
- A barra mostra `Proxy ativo :80 · HTTPS desativado`.
- A CA saiu do Windows, e a pasta das chaves sumiu:

  ```powershell
  Get-ChildItem Cert:\CurrentUser\Root | Where-Object Subject -like '*Severino*'
  Test-Path "$env:LOCALAPPDATA\Severino\ca"
  ```

  Esperado: nenhuma linha no primeiro comando, e `False` no segundo.
- O HTTPS para de responder, e o HTTP continua:

  ```powershell
  curl.exe -sS --ssl-no-revoke https://meuapp.sev/ -o NUL
  curl.exe -s -o NUL -w "%{http_code}`n" http://meuapp.sev/
  ```

  Esperado: o primeiro falha com `Could not connect` / `Couldn't connect to server`, e o segundo imprime `200`.
- As rotas guardam a escolha de HTTPS. Em `config.json`, aberto por Configurações › Abrir pasta, a rota continua com `"https": true`.
- No formulário de rota (Editar), as caixas "HTTPS" e "Redirecionar HTTP→HTTPS" aparecem desabilitadas, com "O HTTPS está desativado. Ativar em Configurações". O link fecha o formulário e abre Configurações.

## 2. Desistir antes e recusar o aviso

Prova o item "recusar o aviso do Windows na ativação desfaz tudo" do checklist.

1. **Desistir no app.** Clique **Ativar HTTPS**. Aparece o diálogo "Ativar HTTPS", com o nome da CA e a impressão digital. Clique **Cancelar**.
   - Esperado: o aviso do Windows **não** aparece, e nada muda: o estado continua "Desativado" e não há mensagem.
2. **Recusar no Windows.** Clique **Ativar HTTPS** › **Continuar**. No "Aviso de Segurança" do Windows, clique **Não**.
   - Esperado: o aviso abre **na frente** do Severino, e a janela do Severino não responde a cliques enquanto ele está aberto.
   - Esperado: a mensagem "O Windows não instalou a CA, então nada mudou.", o estado continua "Desativado", e os comandos do teste 1 seguem mostrando nenhum certificado e `False`.

## 3. Ativar

Prova o critério 1 e o "Ativar de novo" do checklist.

1. **Ativar HTTPS** › leia o diálogo do app e **anote a impressão digital** › **Continuar**.
2. No aviso do Windows, confira:
   - "alega representar" mostra o mesmo nome do diálogo do app, como `Severino Local CA (usuário@máquina)`;
   - a "Impressão digital (sha1)" é **igual** à anotada.
   
   Clique **Sim**.

**Esperado:**

- Só **uma** confirmação do Windows.
- O cartão mostra "Ativo. A CA vale até <data daqui a 10 anos>.", "Cobre: qualquer nome .sev" e a mensagem "HTTPS ativo…".
- A barra mostra `Proxy ativo :80 :443 · HTTPS ok`.
- Todas as rotas passam a ter HTTPS, mas só ganham redirecionamento as que já o tinham.
- A CA está no Windows com a Name Constraint certa:

  ```powershell
  $ca = Get-ChildItem Cert:\CurrentUser\Root | Where-Object Subject -like '*Severino Local CA*'
  $ca | Select-Object Thumbprint, NotAfter
  $ca.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.30' } | ForEach-Object { "crítica: $($_.Critical)"; $_.Format($true) }
  ```

  Esperado: um só certificado, com o `Thumbprint` igual ao anotado sem os espaços. Na extensão, `crítica: True`, "Permitido" com `Nome DNS=sev`, e "Excluído" com o IPv4 `0.0.0.0` e o IPv6 todo zero, cada um com máscara zero.
- As chaves no disco estão cifradas com DPAPI (critério 9):

  ```powershell
  (([IO.File]::ReadAllBytes("$env:LOCALAPPDATA\Severino\ca\ca.key"))[0..7] | ForEach-Object { $_.ToString('X2') }) -join ' '
  ```

  Esperado: `01 00 00 00 D0 8C 9D DF`, o cabeçalho de todo blob DPAPI. Uma chave em claro começaria com `30`.

## 4. Navegação no Edge e no Chrome

Prova o critério 2. Faça no Edge e repita no Chrome.

1. Abra `https://meuapp.sev`.
   - Esperado: a página abre **sem aviso**, com o cadeado normal.
   - Clique no cadeado › conexão segura › certificado. Esperado: emitido para `meuapp.sev`, por `Severino Local CA (…)`, válido por 397 dias.
2. **HTTP/2.** Abra o DevTools (F12) › Network e recarregue. Clique com o botão direito no cabeçalho das colunas › marque **Protocol**.
   - Esperado: `h2` nas requisições do documento.
3. **HMR por `wss://`.** Abra `https://hmr.sev`, ou sua rota Vite. No DevTools › Network › filtro **WS**, recarregue.
   - Esperado: uma conexão `wss://hmr.sev/…` com status `101`. **Confira o endereço:** se o Vite não conseguir passar pelo proxy, ele tenta sozinho `ws://localhost:5173`, e o HMR "funciona" sem testar o Severino.
   - Edite `main.js` (ou `src/main.js`) e salve. Esperado: a página atualiza sem recarregar inteira, e a aba WS mostra mensagens novas.

## 5. Redirecionamento e HSTS

Prova o critério 3.

1. Em Editar, deixe a rota com **HTTPS** e **Redirecionar HTTP→HTTPS** ligados.

   ```powershell
   curl.exe -sI "http://meuapp.sev/x?y=1"
   ```

   Esperado: `HTTP/1.1 307 Temporary Redirect` e `Location: https://meuapp.sev/x?y=1`.
2. Nenhuma resposta do proxy traz HSTS:

   ```powershell
   curl.exe -sI --ssl-no-revoke https://meuapp.sev/ | Select-String -Pattern strict-transport
   curl.exe -sI "http://meuapp.sev/x" | Select-String -Pattern strict-transport
   curl.exe -sI http://127.0.0.1/ | Select-String -Pattern strict-transport
   ```

   Esperado: nenhuma linha nos três. O terceiro é a página 404 do proxy: `127.0.0.1` não tem rota.
3. Desligue **Redirecionar** e salve.

   ```powershell
   curl.exe -s -o NUL -w "%{http_code}`n" http://meuapp.sev/
   ```

   Esperado: `200`, sem redirecionamento. Religue o redirecionamento depois.

## 6. Rota nova coberta, sem confirmação

Prova o critério 4.

1. **Nova rota** › domínio `api.sev`, destino igual ao da rota de teste.
   - Esperado: "HTTPS" e "Redirecionar HTTP→HTTPS" já vêm **marcados**, e não aparece aviso de cobertura.
2. **Criar.**
   - Esperado: **nenhum** aviso do Windows, nenhum diálogo, e o aviso "api.sev pronto".
   - Na lista: selo `https` ao lado de `api.sev`, e o link abre `https://api.sev/`.
3. Abra `https://api.sev` no navegador. Esperado: abre sem aviso.
4. **HSTS preload.** Em **Nova rota**, digite `teste.dev`, sem salvar.
   - Esperado: "HTTPS" marcado e **travado**, com "HTTPS obrigatório: navegadores só abrem este domínio com HTTPS (HSTS preload)." e **sem** o aviso amarelo de HSTS.
   - Cancele o formulário.

## 7. Reemissão

Prova o critério 5. O exemplo usa `api.empresa.com`, um TLD real. Enquanto a rota existir, esta máquina deixa de acessar o site real com esse nome; ela é removida no fim do teste.

1. **Nova rota** › `api.empresa.com`, com o mesmo destino.
   - Esperado: o aviso amarelo "api.empresa.com está fora da CA atual…". Se o nome existir na internet, também aparece o aviso de domínio existente, e o botão vira "Criar mesmo assim".
2. **Criar.** Aparece o diálogo "Reemitir a CA", que diz que a CA não cobre o domínio e avisa que o Windows pede para remover a antiga. Clique **Cancelar**.
   - Esperado: o aviso "api.empresa.com fica sem HTTPS até reemitir em Configurações.".
   - Na lista, o selo `https ⚠`, com a dica "Fora da CA…".
   - A barra mostra `HTTPS: 1 domínio fora da CA`, clicável, que abre Configurações.
   - O cartão HTTPS mostra o aviso "api.empresa.com está fora da CA e fica sem HTTPS até reemitir.".
3. Em Configurações › HTTPS, clique **Reemitir** › confira a impressão digital nova › **Continuar**.
   - Esperado: **duas** confirmações do Windows. A primeira instala a nova; clique **Sim**. A segunda remove a antiga; clique **Sim**. As duas abrem na frente do Severino.
   - Esperado: o cartão mostra "Cobre: api.empresa.com, qualquer nome .sev" e "CA reemitida…". O selo vira `https`, e a barra volta a `HTTPS ok`.
   - Só a CA nova ficou no Windows: o comando do teste 3 mostra **um** certificado, com a impressão digital nova.
   - O `config.json` ganhou `"com": true` em `tldExists`.
4. Abra `https://api.empresa.com`. Esperado: abre sem aviso. Feche e reabra `https://meuapp.sev`: continua abrindo, agora com o certificado assinado pela CA nova.
5. **Reemitir pelo formulário.** Remova a rota `api.empresa.com`, crie de novo e, no diálogo "Reemitir a CA", clique **Continuar** e **Sim** nas duas confirmações. Esperado: o aviso "CA reemitida. api.empresa.com já abre com https://".
6. **Enxugar a cobertura.** Remova a rota `api.empresa.com` e clique **Reemitir** em Configurações. Esperado: "Cobre: qualquer nome .sev" de novo.

## 8. Name Constraints valendo

Prova o critério 6. A parte automatizada é o teste `Windows_rejects_a_leaf_outside_the_constraints_even_when_signed_by_the_key`, que já rodou no passo 0. Aqui a prova é manual, no Chrome.

O script `scripts/name-constraint-probe.cs` assina um certificado para **qualquer** nome com a chave real da CA. O app nunca faz isso. O script serve esse certificado em `127.0.0.1:9443`, só em memória, e não instala nada.

1. Em um terminal, sirva um nome **fora** da CA:

   ```powershell
   dotnet run scripts/name-constraint-probe.cs -- banco.com.br
   ```

   O script imprime "Folha para banco.com.br: FORA da cobertura".
2. Em outro terminal, abra o Chrome com um perfil separado, que mapeia o nome para `127.0.0.1` sem mexer no hosts. A instalação do Chrome pode estar em `$env:LOCALAPPDATA` em vez de `$env:ProgramFiles`.

   ```powershell
   & "$env:ProgramFiles\Google\Chrome\Application\chrome.exe" --user-data-dir="$env:TEMP\severino-probe" --host-resolver-rules="MAP banco.com.br 127.0.0.1, MAP probe.sev 127.0.0.1" https://banco.com.br:9443/
   ```

   - Esperado: uma página de **erro de certificado**, como `NET::ERR_CERT_NAME_CONSTRAINT_VIOLATION` ou `NET::ERR_CERT_INVALID`, e o script registra "handshake recusado pelo cliente". Se aparecer a página "FALHOU", a restrição não foi aplicada: **pare e me avise**.
   - Confirmação pelo Windows: `curl.exe -sS --ssl-no-revoke --resolve banco.com.br:9443:127.0.0.1 https://banco.com.br:9443/` falha com `SEC_E_WRONG_PRINCIPAL`.
3. **Controle.** Pare o script com Ctrl+C e sirva um nome **dentro** da CA:

   ```powershell
   dotnet run scripts/name-constraint-probe.cs -- probe.sev
   ```

   Na mesma janela do Chrome, abra `https://probe.sev:9443/`. Esperado: a página "Controle ok", com cadeado. Isso mostra que o erro anterior veio da restrição, não do servidor de teste.
4. Pare o script e feche essa janela do Chrome.

## 9. Exportar a CA para o Node

Prova o critério 7.

1. Configurações › HTTPS › **Exportar CA (PEM)** › salve como `severino-ca.pem`.
   - Esperado: aparece "Para o Node.js confiar na CA…" com o comando `setx NODE_EXTRA_CA_CERTS "…\severino-ca.pem"` e o botão **Copiar**.
2. O arquivo tem só o certificado, sem a chave:

   ```powershell
   Select-String -Path "$env:USERPROFILE\severino-ca.pem" -Pattern 'BEGIN'
   ```

   Esperado: só `-----BEGIN CERTIFICATE-----`, sem nenhum `PRIVATE KEY`.
3. Clique **Copiar**, cole o comando num terminal e rode. Depois **abra um terminal novo**, porque o `setx` só vale para terminais abertos depois:

   ```powershell
   node -e "fetch('https://meuapp.sev').then(r => console.log(r.status))"
   ```

   Esperado: `200`. O destino da rota precisa estar no ar; um `502` significa que o HTTPS funcionou, mas o servidor de dev está parado.
4. Prova de que é o PEM que faz o Node confiar: num terminal novo, rode `$env:NODE_EXTRA_CA_CERTS = $null` e repita o comando. Esperado: `fetch failed`, com a causa `unable to get local issuer certificate`. O Node usa a lista de CAs dele, não a do Windows.
5. Para desfazer o `setx`:

   ```powershell
   [Environment]::SetEnvironmentVariable('NODE_EXTRA_CA_CERTS', $null, 'User')
   ```

## 10. Porta 443 ocupada

Prova o critério 8.

1. Feche o Severino (bandeja › **Sair**).
2. Num terminal, ocupe a 443 e deixe o terminal aberto:

   ```powershell
   $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 443); $l.Start(); "ocupada pelo PID $PID"
   ```

3. Abra o Severino.
   - Esperado: a barra mostra `Proxy ativo :80 · HTTPS: A porta 443 está em uso por pwsh (PID <o mesmo>). Trocar porta`.
   - O HTTP continua: `curl.exe -s -o NUL -w "%{http_code}`n" http://meuapp.sev/` imprime `200`.
4. Clique no aviso da barra. Esperado: abre Configurações. Em **Porta HTTPS do proxy**, digite `8443` › **Aplicar**, sem reiniciar o app.
   - Esperado: a barra mostra `Proxy ativo :80 :8443 · HTTPS ok`.
   - O link da lista abre `https://meuapp.sev:8443/`, sem aviso.
   - `curl.exe -sI "http://meuapp.sev/x?y=1"` traz `Location: https://meuapp.sev:8443/x?y=1`.
   - Digitar `80` na porta HTTPS mostra "HTTP e HTTPS precisam de portas diferentes.".
5. Libere a porta, com `$l.Stop()` no terminal, e volte a porta HTTPS para `443` › **Aplicar**. Esperado: `Proxy ativo :80 :443 · HTTPS ok`.

## 11. Páginas de erro

Não é critério da fase. Confere as páginas novas, com o Severino na portaria. Olhe em tema claro e escuro: o tema segue o do Windows.

1. **Rota desconhecida.** Abra `http://127.0.0.1/`.
   - Esperado: o cartão "Portaria" com o selo `404`, o rosto do Severino e "Rota não encontrada": "Ninguém avisou a portaria sobre 127.0.0.1.", com a lista das rotas ativas clicável.
2. **Rota desligada.** Desligue `api.sev` na lista. Rota desligada sai do hosts, então o navegador só chega ao proxy enquanto reaproveita uma conexão ou um DNS em cache. Para ver a página sempre, use o perfil de teste do Chrome, que força o nome para `127.0.0.1`:

   ```powershell
   & "$env:ProgramFiles\Google\Chrome\Application\chrome.exe" --user-data-dir="$env:TEMP\severino-probe" --host-resolver-rules="MAP api.sev 127.0.0.1" http://api.sev/
   ```

   - Esperado: "Rota desligada": "api.sev está cadastrada, mas desligada.". Religue a rota depois.
3. **Destino fora do ar.** Pare o servidor de dev da rota de teste e abra a rota.
   - Esperado: o selo `502`, "Destino fora do ar", "meuapp.sev → localhost:<porta> não respondeu." e "O Severino interfonou, mas ninguém atendeu. Seu servidor está rodando?".
4. A página não carrega nada de fora: no DevTools › Network, só aparece a própria página.

## 12. Limpeza

- Remova as rotas de teste: `api.sev`, `hmr.sev` e a de `teste.dev`, se tiver salvado.
- Reemita a CA, para a cobertura voltar só ao que você usa.
- Desfaça o `NODE_EXTRA_CA_CERTS` se não for usar (teste 9, passo 5).
- Apague `$env:TEMP\severino-probe`, o perfil de teste do Chrome.

## Registro de resultados

| # | Teste | Critério | Resultado | Observações |
|---|---|---|---|---|
| 0 | Build sem avisos e testes automatizados | 10 | ☐ | |
| 1 | Remover CA | 9, checklist | ☐ | |
| 2 | Desistir no app e recusar o aviso | checklist | ☐ | O aviso abriu na frente? |
| 3 | Ativar, Name Constraint e chaves DPAPI | 1, 9 | ☐ | Quantas confirmações? |
| 4 | Edge e Chrome, HTTP/2 e HMR por `wss://` | 2 | ☐ | |
| 5 | 307 e nenhum HSTS | 3 | ☐ | |
| 6 | Rota coberta sem confirmação, e HSTS preload | 4 | ☐ | |
| 7 | Reemissão | 5 | ☐ | Quantas confirmações? |
| 8 | Name Constraints no Chrome | 6 | ☐ | Qual erro o Chrome mostrou? |
| 9 | Exportar CA e Node | 7 | ☐ | |
| 10 | Porta 443 ocupada | 8 | ☐ | |
| 11 | Páginas de erro | (extra) | ☐ | |
