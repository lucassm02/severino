# Guia de testes: Fase 10, Ambientes, depuração e projetos

Roteiro manual para fechar a Fase 10. Cobre os critérios de pronto do [spec](../specs/fase-10-ambientes-e-depuracao.md) que os testes automatizados não alcançam. As partes estão na ordem em que foram feitas; cada uma pode ser testada sozinha.

## Convenções

- Os comandos são para **PowerShell 7**, num terminal comum.
- Rode a versão de desenvolvimento com `dotnet run --project src/Severino.App`, depois de sair do Severino instalado pela bandeja.
- Os exemplos usam uma rota `meuapp.sev → localhost:3000`. Qualquer servidor serve; para ter um rápido, numa pasta vazia:

  ```powershell
  npx --yes http-server -p 3000
  ```

## Parte 2. Detalhes de uma requisição

Prova os critérios 1 a 7 da parte 2.

1. **Abrir.** Abra `http://meuapp.sev/` no navegador. Na aba Requisições, clique na linha.
   - Esperado: um painel à direita com **Geral**, **Cabeçalhos** e **Corpo**. Geral mostra hora, status, duração, a rota, para onde foi (`http://localhost:3000/`) e os tamanhos. As colunas Hora e Duração somem da lista enquanto o painel está aberto.
2. **Andar pela lista.** Abra a aba **Cabeçalhos** e clique em outras linhas.
   - Esperado: o painel acompanha a linha e continua em Cabeçalhos.
3. **Valores ocultos.** Mande uma requisição com credencial:

   ```powershell
   curl.exe http://meuapp.sev/ -H "Authorization: Bearer segredo"
   ```

   - Esperado: em Cabeçalhos, `Authorization: ••••••••`. Marcar **Mostrar valores ocultos** mostra `Bearer segredo`.
4. **Sem corpos.** Com **Guardar corpos** desligado, mande um POST:

   ```powershell
   curl.exe -X POST http://meuapp.sev/api -H "Content-Type: application/json" -d '{"pedido":42}'
   ```

   - Esperado: a aba Corpo explica que os corpos não estão sendo guardados. **Repetir** diz que o corpo não foi guardado e não manda nada.
5. **Com corpos.** Ligue **Guardar corpos** e repita o POST.
   - Esperado: a ida mostra o JSON formatado, `"pedido": 42`. A volta mostra o que o servidor respondeu, se for texto.
6. **Copiar como curl.** Nessa linha, **Copiar como curl › Para PowerShell** e cole no terminal.
   - Esperado: o comando roda e aparece como linha nova no log, igual à original.
7. **Repetir.** Clique em **Repetir**.
   - Esperado: "Mandada de novo: …" com o status, e uma linha nova no topo da lista.
8. **Exportar HAR.** No **⋯** da aba, **Exportar HAR**, salve e arraste o arquivo para a aba Network do DevTools do Edge ou do Chrome.
   - Esperado: as requisições aparecem lá, com cabeçalhos e, nas que tinham, os corpos.
9. **Só memória.** Saia do Severino e abra de novo.
   - Esperado: o log começa vazio e **Guardar corpos** volta desligado.

## Parte 7. Avisos de saúde

Prova os critérios 1 a 6 da parte 7. Deixe a janela do Severino escondida (feche-a) para ver os avisos como eles aparecem no dia a dia.

1. **Ligar.** Edite a rota `meuapp.sev`, abra **Avançado** e marque **Avisar quando parar de responder**. Salve. Com o servidor da porta 3000 rodando, espere a rota aparecer como respondendo.
2. **Queda.** Pare o servidor (Ctrl+C) e espere 20 segundos.
   - Esperado: um aviso na bandeja, "meuapp.sev parou de responder", com "Nada responde em localhost:3000 há 15 segundos."
3. **Clique.** Clique no aviso.
   - Esperado: a janela abre na aba Rotas, filtrada por `meuapp.sev`.
4. **Volta.** Suba o servidor de novo.
   - Esperado: em até 10 segundos, "meuapp.sev voltou".
5. **Reinício rápido.** Pare e suba o servidor em menos de 10 segundos.
   - Esperado: nenhum aviso.
6. **Vários.** Marque a opção em mais uma rota ou serviço que use o mesmo servidor, e pare o servidor.
   - Esperado: um aviso só, "2 destinos pararam de responder", com os dois nomes.
7. **Pausado.** Pause o Severino pela bandeja e pare o servidor.
   - Esperado: nenhum aviso. Retome com o servidor parado: nenhum aviso também, porque a rota não respondeu desde que voltou.
8. **Nunca respondeu.** Saia do Severino com o servidor parado e abra de novo.
   - Esperado: nenhum aviso.
9. **Desligar tudo.** Em Configurações › Geral, desligue **Avisos de rotas e serviços**, suba e pare o servidor.
   - Esperado: nenhum aviso. Ligue de novo antes de seguir.

## Parte 5. Regras por rota

Prova os critérios 1 a 4 da parte 5. Use o DevTools do navegador (F12, aba Network) para ver os cabeçalhos.

1. **Cabeçalhos.** Edite `meuapp.sev`, abra **Avançado › Regras** e adicione na ida `X-Tenant: loja` e na volta `Cache-Control: no-store`. Salve e abra a rota.
   - Esperado: no card, os selos **+2 cabeçalhos**. Na aba Requisições, os detalhes mostram `Cache-Control: no-store` na volta. Se o seu servidor mostra os cabeçalhos recebidos, `X-Tenant: loja` está lá.
2. **Tirar um cabeçalho.** Na volta, adicione um cabeçalho que o servidor manda (como `X-Powered-By`) com o valor vazio.
   - Esperado: ele some da resposta.
3. **CORS.** Ligue **Liberar CORS**. Numa página de outra origem (por exemplo `http://localhost:5173`), rode no console do DevTools:

   ```js
   fetch("https://meuapp.sev/", { method: "PUT", credentials: "include", headers: { "Content-Type": "application/json" } }).then(r => r.status)
   ```

   - Esperado: o preflight `OPTIONS` volta 204 marcado como **Severino** na aba Requisições, e o `fetch` responde sem erro de CORS.
4. **Atraso.** Ponha 2000 em **Esperar antes de repassar**.
   - Esperado: o selo **atraso 2000 ms** em cor de alerta, e cada requisição leva cerca de 2 s a mais.
5. **Falha simulada.** Escolha **503** em **100** %.
   - Esperado: o selo **falha 100% com 503**; o navegador mostra a página "Falha simulada" do Severino, que diz como desligar; na aba Requisições, 503 marcado como **Severino**. Com CORS ligado, o `fetch` do passo 3 recebe 503, e não um erro de CORS.
6. **Validação.** Tente um cabeçalho `Host`, um nome com espaço, um atraso de 40000 ou 150 %.
   - Esperado: o erro aparece embaixo das regras e nada é salvo.
7. **Backup.** Exporte o backup em Configurações, apague a rota e importe.
   - Esperado: a rota volta com as mesmas regras.
8. Ao terminar, zere o atraso e a falha.

## Parte 6. Pasta como destino

Prova os critérios 1 a 6 da parte 6. Prepare uma pasta com um site estático:

```powershell
New-Item -ItemType Directory -Force C:\temp\site\guia | Out-Null
Set-Content C:\temp\site\index.html '<h1>início</h1><a href="guia/">guia</a>'
Set-Content C:\temp\site\guia\index.html '<h1>guia</h1><img src="logo.svg">'
```

1. **Escolher.** **Nova rota**, `docs.sev`, destino **Pasta**, **Escolher…** e aponte `C:\temp\site`. Salve.
   - Esperado: o card mostra `→ C:\temp\site` e a rota aparece como respondendo.
2. **Servir.** Abra `http://docs.sev/`.
   - Esperado: "início". O link "guia" abre a página da pasta `guia`. `http://docs.sev/guia` (sem a barra) leva a `http://docs.sev/guia/` com 307, visível na aba Requisições.
3. **Sem cache.** Mude o `index.html` e aperte F5.
   - Esperado: a mudança aparece na hora. Nos detalhes da requisição, `Cache-Control: no-cache` na volta.
4. **Não encontrado.** Abra `http://docs.sev/painel/relatorios`.
   - Esperado: a página "Arquivo não encontrado", que sugere a opção de app de página única.
5. **App de página única.** Ligue **Mandar caminhos desconhecidos para o index.html** e abra o mesmo endereço.
   - Esperado: "início". `http://docs.sev/falta.js` continua 404: só páginas caem no `index.html`.
6. **Caminho.** Crie também `meuapp.sev/docs` como pasta `C:\temp\site`. Abra `http://meuapp.sev/docs/guia/`.
   - Esperado: "guia".
7. **Pasta que some.** Renomeie `C:\temp\site` para `C:\temp\site2`.
   - Esperado: em até 5 segundos a rota aparece como fora do ar; abrir dá a página "A pasta não está lá", com o caminho. Volte o nome: a rota volta sozinha.
8. **Regras.** Ligue **Liberar CORS** e um cabeçalho na volta, como `X-Build: 42`.
   - Esperado: os dois aparecem nas respostas da pasta.
9. **HTTPS.** Com a CA ativa, marque **Usar HTTPS** e abra `https://docs.sev/`.
   - Esperado: abre sem aviso de certificado.

## Parte 1. Ambientes

Prova os critérios 1 a 6 da parte 1. Antes, confira que a barra de status não mostra nenhum ambiente (critério 5).

1. **Primeiro ambiente.** Na aba DNS, **Nova entrada** `sql.banco → 192.168.100.8`, com **Ambiente** `producao`. Salve.
   - Esperado: a entrada ganha o selo **producao**, continua valendo, e a barra de status mostra **Ambiente: producao**.
2. **Mesmo nome, outro ambiente.** Nova entrada `sql.banco → 192.168.203.150`, ambiente `staging` (a sugestão já lista `producao`).
   - Esperado: salva sem erro. A de staging aparece esmaecida, "Em outro ambiente". Uma terceira `sql.banco` **sem** ambiente é recusada.
3. **Trocar pela barra.** Clique em **Ambiente: producao** e escolha **staging**.

   ```powershell
   ping -n 1 sql.banco
   ```

   - Esperado: em cerca de um segundo o ping vai para 192.168.203.150; a de producao fica esmaecida; o interruptor dela continua ligado.
4. **Trocar pela bandeja.** No menu da bandeja, **Ambiente › producao**.
   - Esperado: volta para 192.168.100.8. O menu marca o ambiente ativo.
5. **O estado de cada item é dele.** Desligue a de staging, troque para staging e volte.
   - Esperado: em staging ela continua desligada (nada responde por `sql.banco`); em producao a outra volta como estava.
6. **Rotas e serviços.** Crie `api.sev → localhost:3000` em producao e `api.sev → localhost:4000` em staging. Troque de ambiente com o navegador aberto em `http://api.sev/`.
   - Esperado: cada F5 vai para o servidor do ambiente ativo. O mesmo vale para um serviço com o mesmo nome em dois ambientes.
7. **Nenhum.** Escolha **Nenhum (só os itens sem ambiente)**.
   - Esperado: nada com ambiente vale; os itens sem ambiente continuam.
8. **PowerShell.**

   ```powershell
   Get-SeverinoEnvironment
   Set-SeverinoEnvironment staging
   Set-SeverinoDns sql.banco 192.168.203.151 -Environment staging
   ```

   - Esperado: a lista e o ativo; a troca acontece na janela também; a entrada de staging muda de IP, a de producao não.
9. **WSL** (se usar). Com uma distro em Configurações › WSL e um serviço por ambiente, troque o ambiente.
   - Esperado: o `/etc/hosts` da distro segue o ambiente ativo.
10. **Fim dos ambientes.** Apague as entradas e rotas com ambiente.
   - Esperado: a barra de status e a bandeja não mostram mais nada de ambiente.

## Parte 3. Arquivo do projeto

Prova os critérios 1 a 6 da parte 3.

1. **Gerar.** Na aba Rotas, no **⋯** do grupo `meuapp`, **Salvar como projeto…** e salve em `C:\temp\meuapp\severino.json`. Abra o arquivo num editor.
   - Esperado: `"severino": 3`, `"name": "meuapp"`, as rotas do grupo e os serviços e entradas DNS que elas usam como destino; nenhum `id` nem endereço `127.77`.
2. **Aplicar soltando o arquivo.** Remova as rotas do grupo e solte o `severino.json` na janela do Severino.
   - Esperado: a prévia "Aplicar o projeto meuapp?" lista o que **Entra**, com o destino de cada item. **Cancelar** não muda nada; **Aplicar** traz tudo de volta.
3. **Lista.** Em Configurações › Projetos, o projeto aparece com o número de itens e o caminho do arquivo.
4. **Aplicar de novo.** No arquivo, mude o destino de uma rota, apague outra e salve. **Aplicar de novo**.
   - Esperado: a prévia mostra **Muda** e **Sai**; aplicado, a rota muda e a apagada some. Uma rota que você desligou antes continua desligada.
5. **Sem mudanças.** **Aplicar de novo** sem mexer no arquivo.
   - Esperado: "Nada a mudar".
6. **Conflito.** Crie à mão uma rota com um domínio que o arquivo também tem e aplique de novo.
   - Esperado: a prévia mostra o domínio em **Não entra**, com o motivo; a rota feita à mão continua como está.
7. **Remover.** **Remover** o projeto.
   - Esperado: os itens do projeto saem; os feitos à mão ficam.
8. **PowerShell.**

   ```powershell
   Export-SeverinoProject -Group meuapp -Path C:\temp\meuapp\severino.json
   Import-SeverinoProject C:\temp\meuapp
   ```

   - Esperado: o primeiro grava o arquivo. O segundo lista as mudanças e pergunta antes de aplicar; `-WhatIf` só mostra.
9. **Backup não é projeto.** Tente abrir como projeto um backup exportado em Configurações › Backup.
   - Esperado: a mensagem diz que o arquivo não tem nome de projeto e sugere importar como backup.

## Parte 4. CA confiável no WSL

Prova os critérios 1 a 4 da parte 4. Precisa do HTTPS ativo e de uma distro em Configurações › WSL. As rotas web só chegam ao WSL com a rede espelhada; num `%UserProfile%\.wslconfig`:

```ini
[wsl2]
networkingMode=mirrored
```

1. **Ligar.** Em Configurações › WSL, ligue **Confiar na CA local nas distros**.
   - Esperado: em poucos segundos a linha da distro diz "Confia na CA local". Uma distro parada diz "Recebe a CA local quando ligar".
2. **curl e Node.** Numa rota HTTPS, como `https://meuapp.sev/`:

   ```bash
   curl -sI https://meuapp.sev/ | head -1
   node -e "fetch('https://meuapp.sev/').then(r => console.log(r.status))"
   ```

   - Esperado: os dois respondem sem erro de certificado (abra um terminal novo na distro para o Node ler o `NODE_EXTRA_CA_CERTS`).
3. **Distro parada.** `wsl --shutdown`, abra o Severino e depois a distro.
   - Esperado: em até 30 segundos a linha muda para "Confia na CA local"; o certificado continua lá depois de reiniciar.
4. **Reemitir.** Reemita a CA em Configurações › HTTPS.
   - Esperado: a distro recebe a CA nova; o `curl` do passo 2 continua sem erro.
5. **Desligar.** Desligue a opção.

   ```bash
   ls /usr/local/share/ca-certificates/severino.crt /etc/profile.d/severino-ca.sh
   ```

   - Esperado: os dois arquivos somem.
6. **Tirar a distro, remover a CA, Limpar tudo.** Com a opção ligada, desligue a distro da lista; depois remova a CA; depois use **Limpar tudo**.
   - Esperado: em cada caso o certificado sai da distro.
7. **Erro explicado.** Numa distro sem o pacote `ca-certificates` (por exemplo, uma Alpine mínima).
   - Esperado: a linha da distro diz que não deu para instalar e por quê.
