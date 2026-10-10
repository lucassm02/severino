# Guia de testes: Fase 3, Polimento

Roteiro manual para fechar a Fase 3. Cobre os critérios de pronto do [spec](../specs/fase-3-polimento.md) que ainda não foram verificados. Instalação e desinstalação na máquina de desenvolvimento já passaram, em 2026-10-09, e estão registradas no spec.

Cada teste diz o que fazer, o que tem de acontecer e qual critério ele prova. Anote o resultado na tabela do fim.

## Convenções

- Os comandos são para **PowerShell**, num terminal comum, a não ser quando o passo pedir administrador.
- `curl.exe` (com `.exe`) usa o validador do Windows. Com `https`, ele leva `--ssl-no-revoke`, como no [guia da Fase 2](fase-2-https.md).
- Para fechar o Severino, use **Sair** no ícone da bandeja. Fechar a janela só esconde o app.

## 0. Preparação

A desinstalação de 2026-10-09 zerou o ambiente: foram o Helper de desenvolvimento, a CA e as rotas.

1. **Testes automatizados** (critério 12):

   ```powershell
   dotnet build
   dotnet test
   ```

   Esperado: `0 Aviso(s)`, `0 Erro(s)` e todos os testes aprovados.
2. **Helper de desenvolvimento**, num terminal **como administrador**, na pasta do projeto:

   ```powershell
   ./scripts/dev-helper.ps1 install
   ```

3. **Um servidor de dev rodando**, de preferência um Vite, como no guia da Fase 2.

## 1. Assistente de primeira execução

Prova o critério 2, na máquina de desenvolvimento. O teste 10 repete numa máquina limpa.

Sem rotas na config, o assistente abre sozinho.

```powershell
dotnet run --project src/Severino.App
```

1. **Passo 1 de 2.** Esperado:
   - a janela "Bem-vindo ao Severino", sobre a principal;
   - três itens: "Serviço auxiliar", "Porta do proxy" e "Proxy do sistema", com ✓ e um texto curto em cada.
   - Se algum vier com ✗, a correção ao lado tem de resolver e o item virar ✓ sozinho.
2. **Forçar um ✗ (opcional):** num terminal como administrador, `net stop Severino.Helper`. Em até 15 s, "Serviço auxiliar" vira ✗ com "Tentar de novo". Depois rode `net start Severino.Helper` e clique **Tentar de novo**: o item volta para ✓.
3. **Continuar.** Esperado:
   - o formulário de rota, com o domínio `meuapp.sev` sugerido;
   - a caixa "Usar HTTPS", desmarcada;
   - o botão "Criar e abrir".
4. Troque a porta para a do seu servidor; o combo lista as portas abertas, como no teste 3. Marque **Usar HTTPS** e clique **Criar e abrir**.
   - Esperado: o diálogo "Ativar HTTPS" do app e o aviso do Windows, na frente do Severino.
   - Esperado: o navegador abre `https://meuapp.sev/` sem aviso.
5. Feche e abra o Severino de novo. Esperado: o assistente **não** aparece mais.
6. Configurações › Manutenção › **Rever checagem** › **Abrir**. Esperado: o assistente abre de novo. Feche pelo X; esperado: ele não volta no próximo início.

## 2. Requisições

Prova o critério 4.

1. Abra a rota no navegador, recarregue algumas vezes e vá à aba **Requisições**. Esperado:
   - linhas com hora, método, domínio, caminho, status e duração, com a mais nova no topo;
   - o status colorido por faixa: 2xx verde, 3xx azul, 4xx amarelo, 5xx vermelho.
2. **Respostas do próprio Severino.** Abra `http://127.0.0.1/`, um nome sem rota. Pare o servidor de dev e abra a rota. Esperado: linhas `404` e `502` com o selo "Severino".
3. **WebSocket.** Com o servidor de dev de pé, e um Vite com HMR, recarregue a página. Esperado: uma linha `WS` com status `101` e duração "aberto". Feche a aba do navegador; em segundos, a duração vira um tempo.
4. **Pausar.** Clique **Pausar** e recarregue a página três vezes. Esperado: a lista não muda, e aparece "3 novas". **Retomar** traz as três.
5. **Filtro.** Escolha um domínio no combo. Esperado: só as linhas dele. "Todos os domínios" volta tudo.
6. **Botão direito** numa linha › **Copiar URL**. Esperado: a URL completa na área de transferência. **Abrir no navegador** abre a URL.
7. **Limpar** esvazia a lista. Com nada na lista, aparece "Nenhuma requisição".
8. **Rajada (opcional):** num terminal, troque o endereço pelo da sua rota:

   ```powershell
   1..500 | ForEach-Object -Parallel { curl.exe -s -o NUL http://meuapp.sev/ } -ThrottleLimit 20
   ```

   Esperado: a janela continua respondendo durante a rajada, e a lista fica com no máximo 1000 linhas.

## 3. Portas no formulário

Prova o critério 5.

1. **Nova rota** › abra o combo da porta. Esperado:
   - as portas com algo escutando, no formato `5173 · node (vite)`;
   - as do WSL como `WSL`;
   - nada do Windows: nada abaixo de 1024 além de 80 e 443, nenhum `svchost`, e nenhuma porta do próprio Severino.
2. Suba outro servidor de dev e abra o combo de novo. Esperado: a porta nova aparece sem reabrir o formulário.
3. Digite uma porta à mão, como `50`. Esperado: o texto fica `50`, sem completar sozinho para `5000`.

## 4. Bandeja

Prova o critério 10. O "Pausar" já passou no teste do instalador; aqui entram os estados do ícone.

1. **Normal.** O ícone é o rosto colorido, e a dica diz "Severino · tudo certo".
2. **Problema.** Como administrador, `net stop Severino.Helper`. Em até 15 s: um ponto vermelho no canto do ícone, e a dica com o problema. `net start Severino.Helper` volta ao normal.
3. **Pausado.** Menu › **Pausar**. Esperado:
   - o ícone fica cinza, e a dica diz "Severino · pausado";
   - a barra inferior mostra "Severino pausado. Retomar" e "hosts sem as rotas";
   - o bloco do hosts some.
   
   Menu › **Retomar** desfaz tudo.
4. **Rotas no menu.** As rotas ligadas aparecem no menu, e clicar abre no navegador, com `https://` quando a rota tem HTTPS. Sem rotas ligadas, aparece "Nenhuma rota ligada".

## 5. Iniciar com o Windows

Prova o critério 7.

1. Configurações › Inicialização › ligue **Iniciar com o Windows**.

   ```powershell
   (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').Severino
   ```

   Esperado: `"<caminho>\Severino.exe" --autostart`.
2. Saia do Windows e entre de novo. Esperado: o Severino na bandeja, **sem** abrir a janela, mesmo com "Iniciar minimizado" desligado.
3. Desligue a opção. Esperado: o comando do passo 1 não devolve nada.

## 6. Importar e exportar

Prova o critério 8.

1. Configurações › Manutenção › Rotas › **Exportar** › salve `severino-rotas.json`. Abra o arquivo. Esperado: `"severino": 1` e as rotas, **sem** `"id"`.
2. Edite o arquivo:
   - acrescente uma rota nova, `{ "domain": "importada.sev", "target": "http://localhost:3000" }`;
   - acrescente uma inválida, `{ "domain": "sem ponto", "target": "http://localhost:3000" }`;
   - mude o `target` de uma rota que já existe.
3. **Importar** esse arquivo. Esperado: um resumo com:
   - "1 rota importada.";
   - "Já existiam, e ficaram como estavam:", com o domínio que já existia, que **não** muda de destino;
   - "Não importadas:", com a rota inválida e o motivo.
4. **Reemissão.** Com o HTTPS ativo, importe uma rota com `"https": true` num TLD real, como `api.empresa.com`. Esperado: o diálogo "Reemitir a CA", como no formulário de rota.
5. Importe um arquivo qualquer, que não seja do Severino. Esperado: "Não deu para importar: O arquivo não é uma exportação de rotas do Severino.".

## 7. Proxy do sistema

Prova o critério 6. Durante o teste, a navegação comum fica fora do ar, porque o proxy configurado não existe. Ela volta no passo 5.

1. Configurações do Windows › Rede e Internet › Proxy › Configuração manual do proxy › **Configurar**:
   - "Usar um servidor proxy": ligado;
   - endereço `127.0.0.1`, porta `9`, onde não há proxy;
   - "Não usar o servidor proxy para endereços locais": marcado;
   - **Salvar**.
2. Em até 10 s, a barra inferior do Severino mostra "proxy do sistema no caminho de N domínios", o ícone ganha o ponto vermelho, e a rota **não** abre no Edge e no Chrome.
3. Clique no aviso da barra › **Adicionar exceções**. Esperado:
   - "Exceções adicionadas: *.sev" (e os nomes exatos de TLDs reais, se houver);
   - a tela de proxy do Windows, reaberta, mostra `*.sev` nas exceções;
   - recarregar a rota no navegador funciona, **sem** reiniciar o navegador.
4. Configurações › Manutenção › **Limpar tudo** remove essa exceção. Só faça agora se for fazer o teste 9 também; senão, apague `*.sev` à mão na tela do Windows.
5. **Desfazer:** desligue "Usar um servidor proxy" e salve. A navegação volta.

## 8. Firefox (opcional, exige o Firefox instalado)

Prova o critério 11.

1. Com o HTTPS ativo e o Firefox instalado e aberto uma vez, abra `about:config` e mude `security.enterprise_roots.enabled` para `false`.
2. Feche e abra o Severino. Em Configurações › HTTPS, esperado: o aviso "Firefox", com o nome do perfil e o passo para corrigir.
3. Volte a preferência para `true`, feche e abra o Severino. Esperado: o aviso some, e a rota abre no Firefox sem aviso de certificado.

## 9. Limpar tudo

Prova o critério 9. Remove a CA e a inicialização automática, então faça perto do fim.

1. Ligue "Iniciar com o Windows" e, se fez o teste 7, deixe a exceção `*.sev`.
2. Configurações › Manutenção › **Limpar tudo…** Esperado: o diálogo explicando o que sai, com a caixa "Apagar também as rotas e configurações", desmarcada. Clique **Limpar e fechar** e confirme o aviso do Windows para remover a CA.
3. Esperado: o app fecha, e:

   ```powershell
   Get-ChildItem Cert:\CurrentUser\Root | Where-Object Subject -like '*Severino*'
   (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').Severino
   (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings').ProxyOverride
   Select-String -Path "$env:windir\System32\drivers\etc\hosts" -Pattern 'Severino managed block'
   Test-Path "$env:LOCALAPPDATA\Severino\config.json"
   ```

   Esperado: nada nos quatro primeiros, e `True` no último, porque as rotas ficaram.
4. Abra o Severino. Esperado: as rotas continuam lá, e o HTTPS aparece desativado.

## 10. Instalação numa máquina limpa

Prova os critérios 1, 2 e 3 sem .NET instalado. Usa o Windows Sandbox, que exige Windows Pro ou Enterprise.

1. **Habilitar o Sandbox**, uma vez, como administrador, e reiniciar:

   ```powershell
   Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
   ```

2. Gere o instalador e abra um Sandbox com a pasta dele mapeada, só leitura:

   ```powershell
   ./scripts/build-installer.ps1
   $wsb = Join-Path $env:TEMP 'severino.wsb'
   Set-Content $wsb "<Configuration><MappedFolders><MappedFolder><HostFolder>$PWD\artifacts\installer</HostFolder><SandboxFolder>C:\Instalador</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder></MappedFolders></Configuration>"
   Start-Process $wsb
   ```

3. Dentro do Sandbox, abra `C:\Instalador\Severino-Setup-0.3.0.exe` e instale.
   - Esperado: nenhum pedido de instalar o .NET.
   - Esperado: o Severino abre no fim, e o assistente aparece.
4. Siga o assistente sem ler nada fora da tela. O Sandbox não tem servidor de dev, então aponte a rota para uma porta qualquer.
   - Esperado: a rota é criada.
   - Esperado: o navegador mostra a página de erro "Destino fora do ar" do Severino, o que prova que o nome chegou ao proxy.
5. Desinstale pelas Configurações do Windows › Aplicativos. Esperado: a pergunta sobre apagar as rotas, e nada do Severino depois: nem serviço, nem `C:\Program Files\Severino`.

## 11. Atualizar por cima

Prova o item "atualizar por cima de uma versão anterior preserva as rotas". Pode ser feito no Sandbox do teste 10, antes de desinstalar, ou na máquina de desenvolvimento.

1. Com a 0.3.0 instalada e pelo menos uma rota criada, gere uma versão "mais nova":

   ```powershell
   ./scripts/build-installer.ps1 -Version 0.3.1
   ```

2. Com o Severino aberto, rode `Severino-Setup-0.3.1.exe`. Esperado: o instalador pede para fechar o Severino.
3. Depois de instalar, esperado:
   - Configurações › Sobre mostra "Severino 0.3.1";
   - as rotas continuam lá;
   - o serviço está rodando (`Get-Service Severino.Helper`);
   - o bloco do hosts volta.

## 12. Outra conta aprova o UAC (opcional)

Exige duas contas no Windows: uma comum, que usa o Severino, e uma de administrador.

1. Entre com a conta comum e rode o instalador. No UAC, informe a senha da conta de administrador.
2. Esperado: `AllowedUserSid` em `HKLM\SOFTWARE\Severino\Helper` é o SID da conta comum, e o Severino abre no fim com a conta comum.
3. Desinstale da mesma forma. Esperado: antes de começar, o aviso de que o Severino foi instalado para outro usuário.

## Limpeza

- Desligue o proxy do Windows, se fez o teste 7.
- Volte o Firefox ao normal, se fez o teste 8.
- Reinstale o Helper de desenvolvimento, se algum teste de instalador rodou na máquina de desenvolvimento.

## Registro de resultados

| # | Teste | Critério | Resultado | Observações |
|---|---|---|---|---|
| 0 | Build sem avisos e testes automatizados | 12 | ☐ | |
| 1 | Assistente de primeira execução | 2 | ☐ | |
| 2 | Requisições | 4 | ☐ | |
| 3 | Portas no formulário | 5 | ☐ | |
| 4 | Bandeja: estados e rotas | 10 | ☐ | |
| 5 | Iniciar com o Windows | 7 | ☐ | |
| 6 | Importar e exportar | 8 | ☐ | |
| 7 | Proxy do sistema | 6 | ☐ | |
| 8 | Firefox | 11 | ☐ | |
| 9 | Limpar tudo | 9 | ☐ | |
| 10 | Máquina limpa (Sandbox) | 1, 2, 3 | ☐ | |
| 11 | Atualizar por cima | checklist | ☐ | |
| 12 | Outra conta aprova o UAC | checklist | ☐ | |
