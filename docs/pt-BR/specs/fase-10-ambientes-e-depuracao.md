# Spec: Fase 10, Ambientes, depuração e projetos

Status: aprovada em 2026-10-12 e implementada em 2026-10-13, as sete partes; falta o roteiro manual do [guia de testes](../testes/fase-10-ambientes-e-depuracao.md). As escolhas em "Decisões" são os padrões adotados.

## Objetivo

As fases anteriores deixaram o Severino completo para apontar nomes. Esta fase cuida do que vem depois, no dia a dia:

1. **Ambientes.** O mesmo nome, como `sql.banco`, aponta para produção, homologação ou staging conforme o ambiente ativo, trocado com um clique.
2. **Detalhes de uma requisição.** Cabeçalhos, tempo, tamanho e, se você quiser, o corpo. Com "Copiar como curl", "Repetir" e exportar em HAR.
3. **Arquivo do projeto.** Um `severino.json` no repositório declara as rotas, serviços e entradas DNS do projeto, e quem clona aplica com um clique.
4. **CA confiável no WSL.** `curl` e Node dentro das distros escolhidas confiam nos certificados do Severino.
5. **Regras por rota.** CORS liberado, cabeçalhos extras, atraso e falha simulados.
6. **Pasta como destino.** Uma rota serve os arquivos de uma pasta, sem precisar subir um servidor.
7. **Avisos de saúde.** A bandeja avisa quando uma rota ou serviço para de responder, e quando volta.

Nada aqui muda as decisões fixas: o proxy continua só em loopback, nada fica exposto à rede.

## Ordem de implementação

Cada parte é independente e entra num commit (ou poucos) próprio, com testes e as duas entradas de changelog. A ordem vai da mais isolada para a que mais mexe no modelo:

1. Detalhes de uma requisição (parte 2).
2. Avisos de saúde (parte 7).
3. Regras por rota (parte 5).
4. Pasta como destino (parte 6).
5. Ambientes (parte 1).
6. Arquivo do projeto (parte 3), que já leva o ambiente de cada item.
7. CA confiável no WSL (parte 4).

O guia de testes manuais fica em `docs/pt-BR/testes/fase-10-ambientes-e-depuracao.md`, escrito ao fim de cada parte.

---

## Parte 1. Ambientes

### Critérios de pronto

1. **Marcar.** Rotas, serviços e entradas DNS ganham um campo opcional **Ambiente** no editor, com sugestões dos ambientes que já existem. Vazio quer dizer "vale em todos".
2. **Mesmo nome, ambientes diferentes.** Dá para ter `sql.banco → 192.168.100.8` em `producao` e `sql.banco → 192.168.203.150` em `staging`. O mesmo nome sem ambiente e com ambiente continua sendo conflito, como hoje.
3. **Trocar.** A barra de status mostra o ambiente ativo, e clicar nela abre a lista. A bandeja tem um submenu **Ambiente** com a mesma lista. Trocar vale na hora: hosts, bloco DNS, proxy, serviços e WSL seguem o ambiente novo em cerca de um segundo.
4. **Fora do ambiente fica desligado, não apagado.** Um item de outro ambiente aparece na lista esmaecido, com o selo do ambiente, e o interruptor dele guarda o estado que tinha. Ao voltar para o ambiente dele, volta como estava.
5. **Sem ambientes, nada muda.** Enquanto nenhum item tiver ambiente, a barra de status e a bandeja não mostram nada novo.
6. **PowerShell.** `Get-SeverinoEnvironment` lista os ambientes e diz qual está ativo; `Set-SeverinoEnvironment staging` troca.

### Como funciona

- `RouteEntry`, `ServiceRoute` e `DnsEntry` ganham `Environment` (texto, vazio por padrão). `AppSettings` ganha `ActiveEnvironment` (vazio = nenhum).
- A lista de ambientes é a dos valores distintos usados nos itens, em ordem alfabética. Não há cadastro à parte: um ambiente existe enquanto algum item o usa.
- Um item **vale** quando está ligado e o ambiente dele é vazio ou o ativo. Uma função só, em Core (`Environments.IsActive`), decide isso, e todos os consumidores passam por ela: o proxy, o monitor de saúde, o `HostsSync`, o `DnsSync`, os encaminhadores de serviço, o WSL, os port-forwards e o acompanhamento do cluster.
- Validação: dois itens com o mesmo nome só convivem se os dois tiverem ambiente e os ambientes forem diferentes. Vale para nome × nome dentro de rotas, serviços e DNS, e entre eles.
- Com `ActiveEnvironment` vazio e algum item com ambiente, só os itens sem ambiente valem. Ao marcar o primeiro item com um ambiente, ele vira o ativo, para a pessoa não ver o item sumir.
- O backup e o arquivo do projeto levam o ambiente de cada item.

### Decisões

- **Etiqueta por item, não variantes de um item.** Cada ambiente é um item próprio (um `sql.banco` por ambiente). Variantes dentro de um item seriam mais compactas, mas mudariam todos os editores e validações; a etiqueta reaproveita tudo.
- **Nome na interface:** "Ambiente" / "Environment".

---

## Parte 2. Detalhes de uma requisição

### Critérios de pronto

1. **Abrir.** Clicar numa linha da aba Requisições abre um painel à direita com três seções: **Geral** (URL, rota, destino, status, tempo, tamanhos), **Cabeçalhos** (ida e volta) e **Conteúdo**.
2. **Cabeçalhos sempre.** Os cabeçalhos da ida e da volta ficam guardados para toda requisição HTTP. `Authorization`, `Cookie`, `Set-Cookie` e `Proxy-Authorization` aparecem mascarados, com um botão para mostrar.
3. **Conteúdo quando pedido.** **Capturar conteúdo**, no menu **⋯** da aba e na aba Conteúdo dos detalhes, desligado por padrão, passa a guardar até 64 KB de cada corpo de ida e volta, só de tipos de texto (JSON, texto, XML, formulário, JavaScript, HTML). JSON aparece formatado. Outros tipos mostram só o tamanho.
4. **Copiar como curl.** Monta o comando com método, URL, cabeçalhos e, se guardado, o corpo, pronto para colar no PowerShell (`curl.exe`).
5. **Repetir.** Manda a mesma requisição de novo pelo proxy; ela aparece como linha nova no log. Fica indisponível para WebSocket, conexões TCP e requisições com corpo que não foi guardado.
6. **Exportar HAR.** Salva as linhas visíveis (respeitando o filtro de domínio) num `.har` que o DevTools do navegador abre.
7. **Só memória.** Nada disso vai para o disco, a não ser pelo "Exportar HAR" que a pessoa pediu.

### Como funciona

- `RequestEntry` ganha: cabeçalhos de ida e volta (até 16 KB por entrada; o resto é cortado com aviso), o destino usado (URL do servidor ou da pasta), o id da rota, o tamanho da ida (`Content-Length` ou contado) e o da volta (contado), e os corpos quando guardados.
- Os corpos são capturados por um fluxo que copia os primeiros 64 KB enquanto repassa tudo, sem segurar a requisição: streaming, SSE e HMR continuam funcionando. A ida usa o mesmo princípio sobre o corpo de entrada.
- Repetir usa um `HttpClient` próprio que fala com o proxy em `127.0.0.1` na porta do esquema original, com o `Host` original e, em HTTPS, o SNI certo.
- HAR 1.2, com `timings` só de `wait` (o Severino não mede DNS nem conexão separadamente).

### Decisões

- **Painel lateral**, não janela: dá para ir descendo a lista com o painel aberto.
- **Limites:** 1000 entradas, 16 KB de cabeçalhos e 64 KB por corpo. No pior caso, cerca de 140 MB de memória com corpos ligados; por isso eles vêm desligados.

---

## Parte 3. Arquivo do projeto (`severino.json`)

### Critérios de pronto

1. **Aplicar.** Três caminhos: arrastar o `severino.json` para a janela, **Configurações › Projetos › Abrir projeto…**, ou `Import-SeverinoProject` no PowerShell (pasta atual por padrão).
2. **Prévia antes.** Uma janela mostra o que entra, o que muda e o que sai, item por item, antes de aplicar. Nada é aplicado sem confirmação, e o Severino nunca lê o arquivo sozinho.
3. **Aplicar de novo atualiza.** Os itens de um projeto ficam marcados com o nome dele. Aplicar o mesmo projeto outra vez atualiza o que mudou, adiciona o que é novo e remove o que saiu do arquivo, sem mexer em itens de fora do projeto.
4. **Lista de projetos.** Configurações › Projetos lista os projetos aplicados, com quantos itens cada um tem, **Aplicar de novo** (do mesmo caminho) e **Remover** (tira todos os itens do projeto).
5. **Gerar.** **Salvar como projeto…** no menu de um grupo de rotas, e `Export-SeverinoProject -Group meuapp` no PowerShell, escrevem um `severino.json` com as rotas do grupo e os serviços e entradas DNS que elas usam como destino.
6. **Conflito explicado.** Um item do arquivo cujo nome já é usado aqui fora do projeto não entra; a prévia diz qual e por quê.

### Como funciona

- O formato é o do backup (versão 3), com `name` (o nome do projeto) e sem ids nem endereços de loopback, que são por máquina:

  ```json
  {
    "severino": 3,
    "name": "meuapp",
    "routes": [{ "domain": "meuapp.sev", "target": "http://localhost:3000", "https": true }],
    "services": [],
    "dnsEntries": []
  }
  ```

- Itens ganham `Project` (texto, vazio por padrão). A chave de atualização é o domínio e o caminho (rotas) ou o primeiro nome (serviços e DNS).
- Configurações guarda o caminho de cada projeto aplicado (`State.Projects`), para **Aplicar de novo**.
- Uma entrada DNS de IP público continua pedindo aprovação, como hoje. A prévia mostra os destinos de cada rota, para a pessoa ver para onde o projeto aponta.

### Decisões

- **Sem vigiar o arquivo.** Aplicar é sempre um gesto da pessoa: um arquivo vindo de um repositório não muda o hosts sozinho.
- **Nome do arquivo:** `severino.json`, na raiz do projeto. Qualquer nome funciona ao abrir pelo diálogo.

---

## Parte 4. CA confiável no WSL

### Critérios de pronto

1. **Ligar.** Em Configurações › WSL, **Confiar na CA local nas distros** instala o certificado raiz do Severino no repositório de certificados de cada distro escolhida.
2. **Funciona com as ferramentas comuns.** Dentro da distro, `curl https://meuapp.sev` e `node` (via `NODE_EXTRA_CA_CERTS`) confiam no certificado sem `-k`.
3. **Acompanha a CA.** Reemitir a CA atualiza as distros; remover a CA, desligar a opção, tirar a distro da lista, **Limpar tudo** e o desinstalador tiram o certificado delas.
4. **Estado por distro.** A lista de distros mostra se a CA está instalada, ou o erro (por exemplo, distro sem `update-ca-certificates`).

### Como funciona

- O script roda como root, como o do `/etc/hosts`:
  - Debian, Ubuntu e Alpine: `/usr/local/share/ca-certificates/severino.crt` e `update-ca-certificates`.
  - Fedora, RHEL e Arch: `/etc/pki/ca-trust/source/anchors/severino.crt` e `update-ca-trust`.
  - `/etc/profile.d/severino-ca.sh` exporta `NODE_EXTRA_CA_CERTS` para o mesmo arquivo.
- Só a parte pública da CA vai para a distro.
- O texto da opção explica que as rotas web só chegam ao WSL com a rede espelhada (`networkingMode=mirrored`), porque o proxy escuta só no loopback do Windows. As rotas de serviço já funcionam sem isso.

### Decisões

- **Desligado por padrão**, mesmo com distros escolhidas: instalar uma raiz no sistema da distro é algo que a pessoa deve pedir.

---

## Parte 5. Regras por rota

### Critérios de pronto

1. **No editor da rota**, uma seção **Regras**, abaixo de Avançado e aberta quando a rota já tem alguma, com:
   - **Liberar CORS:** responde o preflight `OPTIONS` com 204 e acrescenta `Access-Control-Allow-*` às respostas, refletindo a origem pedida, com credenciais.
   - **Cabeçalhos na ida** e **na volta:** pares nome e valor; valor vazio remove o cabeçalho.
   - **Atraso:** de 0 a 30 000 ms antes de repassar.
   - **Falha simulada:** um status (429, 500, 502, 503 ou 504) e uma porcentagem de 0 a 100.
2. **Visível na lista.** O card da rota mostra um selo por regra ligada ("CORS", "+2 cabeçalhos", "atraso 800 ms", "falha 20%"), para ninguém esquecer uma simulação ligada.
3. **No log.** Uma falha simulada aparece na aba Requisições marcada como respondida pelo Severino, com a página de erro dizendo que foi simulada.
4. **Backup e projeto** levam as regras.

### Como funciona

- `RouteEntry` ganha `Rules`: `Cors`, `RequestHeaders`, `ResponseHeaders` (listas de nome e valor), `DelayMs`, `FailStatus` e `FailPercent`.
- Cabeçalhos viram transforms do YARP (`RequestHeader`, `ResponseHeader`, com `Set` ou remoção).
- CORS, atraso e falha ficam num middleware do pipeline do YARP, lendo as regras pelo id da rota guardado nos metadados.
- Nomes de cabeçalho seguem a sintaxe HTTP; `Host` e os de conexão (`Connection`, `Transfer-Encoding`, `Content-Length`) não podem ser mudados.

---

## Parte 6. Pasta como destino

### Critérios de pronto

1. **Escolher.** O destino da rota ganha a opção **Pasta**, com um seletor de pasta, ao lado de **Servidor**.
2. **Servir.** `https://docs.sev` serve os arquivos da pasta, com `index.html` para pastas e o tipo de conteúdo pela extensão. Sem listagem de diretório.
3. **App de página única.** A opção **Mandar caminhos desconhecidos para o index.html** faz `/painel/relatorios` cair no `index.html` quando não há arquivo com esse nome, para apps React, Vue e Angular.
4. **Caminhos.** Rotas por caminho também podem ser pasta: `meuapp.sev/docs → C:\projetos\docs\dist`.
5. **Saúde.** A rota fica "respondendo" enquanto a pasta existe. Se a pasta sumir, a página de erro diz qual pasta faltou.
6. **Sem cache.** As respostas vão com `Cache-Control: no-cache`, para um build novo aparecer no próximo F5.

### Como funciona

- O destino é guardado como URI `file:///C:/projetos/site/dist`. `RouteRules.TryParseTarget` passa a aceitar `file:` com caminho absoluto local; o editor não exige que a pasta exista, só avisa, como faz com porta sem nada escutando.
- A rota de pasta não vira cluster do YARP: um endpoint próprio, antes do `MapReverseProxy`, atende o host e o caminho dela com `StaticFileMiddleware` e um `PhysicalFileProvider` por rota, recriado quando a rota muda.
- Arquivos fora da pasta (`..`, links) não são servidos; o `PhysicalFileProvider` já recusa.
- Regras por rota (parte 5) valem para pasta também, menos os cabeçalhos de ida.

---

## Parte 7. Avisos de saúde

### Critérios de pronto

1. **Por item.** Rotas e serviços ganham **Avisar quando parar de responder** no editor, desligado por padrão.
2. **Avisa uma vez, sem alarme falso.** Um item que estava respondendo e para por 15 segundos seguidos gera uma notificação na bandeja: "meuapp.sev parou de responder (localhost:3000)". Quando volta, outra: "meuapp.sev voltou". Reinícios rápidos do servidor de desenvolvimento não avisam.
3. **Agrupa.** Dois ou mais ao mesmo tempo viram uma notificação só, com os nomes: "3 destinos pararam de responder". Os avisos do Windows se substituem um ao outro, então avisos separados esconderiam os primeiros. Se uma queda e uma volta caem juntas, a queda é a notícia.
4. **Silencioso quando não faz sentido.** Nada é avisado com o Severino pausado, nem de um item que nunca respondeu desde que o app abriu.
5. **Clicar** na notificação abre a janela na aba do item.
6. **Desligar tudo.** Configurações › Geral ganha **Avisos de rotas e serviços**, ligado por padrão, que vale para todos os itens.

### Como funciona

- `RouteEntry` e `ServiceRoute` ganham `NotifyHealth`; `AppSettings` ganha `HealthNotifications`.
- Um `HealthNotifier` no App ouve o `HealthMonitor.Changed`, guarda quando cada item caiu e só avisa quando a queda passa de 15 s, conferindo de novo o estado antes de avisar.

---

## Fora do escopo

- Testar no celular ou em outra máquina pela rede: o proxy continua só em loopback.
- Editar a requisição antes de repetir: "Repetir" manda igual. Para mudar, "Copiar como curl".
- Respostas falsas (mocks) além da falha simulada.
- Vigiar o `severino.json` e aplicar sozinho.
- Variantes de um mesmo item por ambiente (ver Decisões da parte 1).
