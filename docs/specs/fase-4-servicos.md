# Spec: Fase 4, Serviços (Kubernetes, Docker e WSL)

**Status:** aprovado em 2026-10-10, com os padrões das "Decisões a confirmar" e as respostas das "Perguntas em aberto"
**Base:** pedido de 2026-10-09: "o meu serviço local chama direto o Kubernetes, pelo IP do entry point do gateway, na porta do serviço, mas com o nome correto", estendido para Docker e WSL. É uma fase nova, que diverge do [planejamento](../planejamento.md): ele deixava containers e WSL fora do escopo. A divergência e a mudança do roadmap estão registradas lá. Segue o formato dos specs anteriores.
**Pré-requisito:** a Fase 3 concluída.

## Objetivo

Um app rodando na máquina, no Windows ou numa distro do WSL, chama um serviço pelo mesmo nome e porta que usaria dentro do cluster ou da rede do Compose, e chega ao destino certo, sem mudar a configuração do app:

- `http://algarbffapi` ou `http://algarbffapi.staging.svc.cluster.local` chegam ao service do cluster, pelo IP do gateway na NodePort dele (`192.168.203.100:32359`), com o nome original na requisição;
- `postgres.database:5432` chega à NodePort do Postgres (`192.168.203.100:30711`);
- `orchestrator:4000`, o nome do serviço no Compose, chega à porta publicada do container (`24600`).

Os serviços e containers são descobertos pelo próprio Severino, rodando `kubectl` e `docker` no Windows ou dentro das distros do WSL. Quando a descoberta automática não servir, dá para colar a saída dos comandos.

## O cenário de referência

Levantado na máquina de desenvolvimento, em 2026-10-09:

- **Windows:** não tem `kubectl` nem `docker`.
- **Distro Ubuntu-22.04 no WSL2:**
  - tem `kubectl` em `/usr/local/bin` e Docker Engine 29 nativo, sem Docker Desktop;
  - o contexto atual do `kubectl` é `kubernetes-admin@kubernetes`, com namespace `staging`.
- **Rede do WSL:** modo NAT, o padrão, com `localhostForwarding=true`. Dentro da distro, `127.0.0.1` é o loopback do WSL, não o do Windows.
- **PATH:** o `docker` só aparece com um shell de login (`bash -lc`). Um `--exec` direto não carrega o PATH completo.
- **Servidor de dev:** o da rota `meuapp.sev` é o container `meuapp-orchestrator` (serviço `orchestrator` do Compose `meuapp`), no Docker da distro, publicado em 24600 e repassado ao Windows pelo `wslrelay`. Quem chama os serviços do cluster pode estar no Windows ou no WSL; containers ficam de fora, decidido em 2026-10-10.

## Critérios de pronto

1. **Descoberta no WSL.** Com a distro rodando, "Importar serviços" lista os services do contexto atual do `kubectl` da distro e os containers do Docker da distro, sem nenhum comando digitado.
2. **Descoberta no Windows.** Com `kubectl` ou `docker` no Windows, eles aparecem como outra fonte. O Docker Desktop, que é o mesmo engine visto do Windows e das distros, aparece uma vez só.
3. **Colar.** Colar a saída de `kubectl get svc -A -o json` ou de `docker ps --format json` produz a mesma lista da descoberta automática.
4. **Kubernetes pela NodePort.** No Windows, com a VPN, depois de importar `algaractivationmicroservice` e `postgres` do cluster de referência:
   - `curl http://algaractivationmicroservice/` e `curl http://algaractivationmicroservice.staging.svc.cluster.local/` recebem a mesma resposta que a NodePort `192.168.203.100:32366`;
   - `psql -h postgres.database -p 5432` conecta.
5. **Docker pelo nome do Compose.** `curl http://orchestrator:4000/` no Windows recebe a resposta do `meuapp-orchestrator`, publicado em 24600.
6. **Chamadores no WSL.** Os mesmos comandos dos critérios 4 e 5, rodados dentro da distro, funcionam.
7. **Atualizar.** Depois de recriar um container com outra porta publicada, "Atualizar" corrige a rota sem recadastrar nada.
8. **Remover.** Remover as rotas de serviço tira os nomes do hosts do Windows e do bloco na distro, e libera as portas.
9. **Testes.** `dotnet test` passa, e o build segue sem avisos.

## Validações iniciais

- **Loopback dedicado.** Confirmar no Windows:
  - um socket em `127.77.0.2:5432` aceita conexões, e um nome no hosts apontando para esse endereço resolve no Edge, no Chrome, no `curl` e no .NET;
  - isso convive com um Postgres local escutando em `0.0.0.0:5432`, inclusive quando ele usa `SO_EXCLUSIVEADDRUSE`.
- **Comandos no WSL.** `wsl.exe -d <distro> --exec bash -lc '<comando>'`:
  - o tempo de resposta com a distro já rodando;
  - a saída em UTF-8 com `WSL_UTF8=1` (a lista de distros sai em UTF-16 sem isso);
  - os erros de autenticação do `kubectl` (plugins `exec`, tokens expirados) aparecem de forma legível.
- **hosts da distro.** Com `generateHosts` ligado, o padrão, o WSL regera o `/etc/hosts` ao iniciar a distro. Confirmar se um bloco gerenciado sobrevive a isso ou se precisa ser regravado a cada início, e se escrever como root (`wsl -u root`) funciona sem pedir senha.
- **Alcance a partir do WSL.** De dentro da distro, em modo NAT, confirmar quais destinos respondem:
  - o IP do gateway do cluster;
  - as portas publicadas pelo Docker da distro;
  - o IP de um container na rede `bridge`.
- **Gateway com encaminhamento TCP.** Contra o cluster de verdade, confirmar que um repasse de bytes sem tocar no HTTP entrega o `Host` original, e que o gateway roteia por ele.

**Resultados (2026-10-09, na máquina de referência):**

- **Loopback dedicado:**
  - um socket em `127.77.0.2` abre e aceita conexões;
  - convive com outro programa na mesma porta em `0.0.0.0`, aberto antes ou depois do Severino;
  - **não** abre quando o outro programa usa `SO_EXCLUSIVEADDRUSE`. Nesse caso a rota de serviço mostra o conflito com o dono da porta, como a porta 80 já faz.
  
  Falta conferir a resolução pelo hosts no Edge, no Chrome e no `curl`, o que depende do Helper aceitar `127/8` (passo 2).
- **Comandos no WSL:** com a distro rodando, `wsl.exe -d Ubuntu-22.04 --exec bash -lc 'true'` leva 0,17 s, e `WSL_UTF8=1` deixa a saída em UTF-8.
- **`kubectl` sem acesso ao cluster:** a API (`https://192.168.203.100:6443`) não respondeu. Provavelmente fica atrás de VPN ou da rede do escritório. O `kubectl` levou **82 s** para desistir: ele repete a descoberta da API cinco vezes, e o `--request-timeout` não limita isso. A descoberta do Severino precisa de um limite de tempo próprio, que encerra o processo (10 s), e de uma mensagem clara: "o cluster não respondeu; a VPN está conectada?". As rotas já importadas não dependem do `kubectl`, só do gateway.
- **Docker:** Docker Engine 29 na distro, sem Docker Desktop, com um container: `meuapp-orchestrator`, do Compose `meuapp`, serviço `orchestrator`, publicando `0.0.0.0:24600 → 4000`. É o destino da rota `meuapp.sev` de hoje, que chega ao Windows pelo `wslrelay`. O `docker ps --format json` já traz os rótulos do Compose. O `docker inspect` só é preciso para o IP do container, no caso de chamadores dentro do WSL.
- **`/etc/hosts` da distro:** é gerado pelo WSL (`generateHosts` ligado, o padrão), então um bloco gravado ali some quando a distro reinicia. O Severino precisa notar o reinício e regravar: ele confere o bloco quando a distro aparece rodando de novo.
- **Root na distro:** `wsl -u root` funciona sem senha, então o bloco do `/etc/hosts` dispensa qualquer pedido no Windows.
- **Rede da distro:** modo NAT; o Windows, visto de dentro da distro, é `172.24.16.1`.
- **Cluster, com a VPN (2026-10-10):**
  - a descoberta respondeu em 0,5 s;
  - um contexto só, kubeadm, com quatro nós: o master em `192.168.203.100` e os workers em `.197` a `.199`;
  - 119 services, sendo 110 **NodePort** e 9 ClusterIP;
  - quase todos (103) são HTTP na porta 80. Os outros são TCP puro: Postgres 5432, Redis 6379 (dois) e Memcached 11211;
  - o controlador `ingress-nginx` escuta em `192.168.203.100`, portas 80 e 443, por `externalIPs`;
  - 83 Ingress, todos nginx e sem TLS, quase todos no mesmo host, `staging.pagtel.com.br`, separados por caminho. O nome de cada service não aparece no Ingress. Pela internet, esse host é um CNAME para o F5 Distributed Cloud (`ves.io`).
- **Alcance:** do Windows e do WSL, pela VPN:
  - a NodePort de um service com pods prontos responde em menos de 0,6 s, com HTTP;
  - a NodePort do Postgres responde, com TCP puro;
  - um service sem pods (`algarbffapi`) não conecta pela NodePort e dá `503` pelo Ingress. Só 75 dos 105 services do `staging` tinham pods prontos.
- **Modo NodePort:** é o "IP do gateway na porta do serviço" do pedido. Qualquer nó atende qualquer NodePort, então o repasse TCP não depende do `Host`. Isso dispensou a validação de roteamento por `Host` que estava pendente.
- **Tradução no WSL:** validada na distro, com as regras criadas e apagadas no mesmo teste.
  - O `iptables` é o 1.8.7, com backend `nf_tables`.
  - Com `route_localnet=1`, uma cadeia `nat` ligada a `OUTPUT` com `DNAT` e um `MASQUERADE` em `POSTROUTING` para origem `127/8` fora do `lo`:
    - `127.77.0.2:80` chegou à NodePort do cluster (resposta do app em 30 ms);
    - `127.77.0.3:4000` chegou à porta publicada 24600 do container (`200` em 5 ms).
  - Sem o `MASQUERADE`, o pacote sairia com origem `127.0.0.1`, que nenhum destino aceita.

## Escopo

### Rota de serviço

Um tipo novo de rota, ao lado da rota web atual:

- **Nomes:** vários por rota. Kubernetes gera `svc`, `svc.ns`, `svc.ns.svc` e `svc.ns.svc.cluster.local`. Docker gera o nome do serviço no Compose e o nome do container.
- **Endereço:** cada rota de serviço ganha um endereço de loopback próprio, na faixa `127.77.0.0/16`, guardado na config para não mudar entre execuções. O hosts aponta os nomes da rota para esse endereço, só em IPv4.
- **Portas:** pares "porta do serviço → destino:porta", como `8080 → 10.20.0.5:8080` ou `5432 → 127.0.0.1:15432`. O Severino escuta no endereço da rota, em cada porta do serviço.
- **Encaminhamento:** TCP puro, byte a byte, nos dois sentidos.
  - Funciona para HTTP, gRPC, TLS e bancos.
  - Mantém o nome que o app usou: o `Host` do HTTP e o SNI do TLS chegam ao destino como saíram do app, que é o "nome correto" que o gateway espera.
  - Como cada rota tem o próprio endereço, dois serviços na mesma porta não se misturam, mesmo sem nome visível na conexão, como num Postgres.
- **Origem:** cada rota guarda de onde veio, por exemplo "Kubernetes · kubernetes-admin@kubernetes · WSL Ubuntu-22.04", para o "Atualizar".
- **Saúde e log:** um teste de conexão TCP por destino, como nas rotas web. Na aba de requisições, uma linha por conexão, com os bytes e a duração.

A rota web atual não muda.

### Mudanças no Helper e no validador

- **Nomes de uma parte só**, como `db` e `pedidos`, passam a valer, mas só em rotas de serviço. Numa rota web continuam recusados.
- **Protocolo do pipe, versão 2:** a sincronização leva pares "nome → endereço" em vez de só nomes. O Helper revalida que todo endereço está em `127.0.0.0/8`, que continua sendo loopback.
  - O modelo de segurança se mantém: o pior que outro programa consegue é desviar um nome para a própria máquina.
  - O Helper nunca grava o IP do gateway no hosts do Windows. Quem fala com o gateway é o proxy.

### Descoberta

**Fontes.** Cada fonte é um par "ferramenta + onde roda":

- **Onde:** o Windows, se a ferramenta estiver no PATH, e cada distro do WSL rodando, consultada por `wsl.exe -d <distro> --exec bash -lc`.
- **Distros paradas:** não são iniciadas sozinhas; aparecem com "Procurar também (inicia a distro)".
- **Docker repetido:** fontes que levam ao mesmo engine Docker, como o Docker Desktop visto do Windows e das distros, são reconhecidas pelo ID do engine (`docker info`) e mostradas uma vez.

**Kubernetes:**

- **Comandos:**
  - `kubectl config get-contexts -o name` lista os contextos;
  - `kubectl get svc -A -o json --context <ctx>` lista os services;
  - `kubectl get endpointslices -A -o json` diz quais têm pods prontos;
  - `kubectl get nodes -o json` dá os IPs dos nós.
  
  Cada comando tem 10 s para responder, ou o processo é encerrado (ver "Resultados").
- **Destino, por service, conforme o tipo:**
  - **NodePort**, o caso do cluster de referência: o IP de um nó e o `nodePort` da porta. O nó padrão é o que hospeda a API do contexto, e dá para escolher outro uma vez por contexto, porque qualquer nó responde por qualquer NodePort.
    - `algarbffapi:80` vira `192.168.203.100:32359`;
    - `postgres.database:5432` vira `192.168.203.100:30711`.
  - **LoadBalancer:** o IP em `status.loadBalancer.ingress`, na porta do serviço.
  - **Só ClusterIP:** aparece como "sem acesso de fora", com a dica do `port-forward` da Fase 6.
- **Pods prontos:** a lista mostra quais services não têm nenhum pod pronto agora. No cluster de referência, eram 30 dos 105 do `staging`. Importar continua permitido, mas a rota nasce avisando.
- **Nome público pelo gateway interno (Ingress, opcional):** os hosts dos Ingress, como `staging.pagtel.com.br`, podem virar **rotas web** comuns apontando para o IP do controlador de Ingress (`192.168.203.100`, por `externalIPs`). Assim o nome público, que pela internet passa pelo F5 Distributed Cloud, vai direto ao gateway pela VPN. É o mesmo tipo de rota que o Severino já tem; o aviso de "domínio existe na internet" continua valendo.

**Docker:**

- **Comando:** `docker ps --format json`, que já traz as portas publicadas e os rótulos do Compose (`com.docker.compose.service`, `com.docker.compose.project`). O DNS interno do Docker não entra: só as portas publicadas no host contam, como decidido em 2026-10-10.
- **Só portas publicadas.** O destino é a porta publicada:
  - com o Docker no Windows ou no Docker Desktop, `127.0.0.1:<porta publicada>`;
  - com o Docker numa distro, a mesma porta, que chega ao Windows pelo `localhostForwarding`.
  
  Portas não publicadas aparecem como "não publicada", com a dica de publicar.

**Colar:** um campo aceita a saída de `kubectl get svc -A -o json`, de `kubectl get ingress -A -o json`, de `docker ps --format json` ou de `docker inspect`, e reconhece o formato sozinho.

**Tela "Importar serviços":**

- escolhe a fonte;
- lista os services e containers com caixas de seleção, mostrando os nomes que serão criados, as portas, o destino e se há pods prontos;
- com uma busca e um filtro por namespace, porque um cluster como o de referência tem mais de cem services;
- avisa conflitos com rotas existentes;
- cria as rotas agrupadas pela origem.

"Atualizar" refaz a descoberta da origem e corrige destinos e portas, sem mexer em nomes que a pessoa editou.

### Chamadores no WSL

Um app dentro da distro não enxerga o loopback do Windows no modo NAT, então o hosts do Windows não serve para ele. E apontar o nome direto para o destino também não basta, porque a porta muda: o app chama `algarbffapi:80`, e o destino é `192.168.203.100:32359`.

A distro faz a mesma tradução que o Severino faz no Windows, com o próprio kernel. Para as distros escolhidas, o Severino escreve como root, pelo `wsl -u root`, sem pedir administrador no Windows:

1. **Um bloco no `/etc/hosts`** da distro, com os mesmos nomes apontando para o mesmo endereço `127.77.x.y` da rota.
2. **Regras de `iptables`** numa cadeia própria (`SEVERINO`, na tabela `nat`, ligada a `OUTPUT`), uma por porta: `127.77.x.y:80` → `192.168.203.100:32359`. Para isso, o Severino:
   - liga o `net.ipv4.conf.all.route_localnet`, que deixa o kernel mandar para fora um pacote destinado a `127/8`;
   - acrescenta um `MASQUERADE` em `POSTROUTING` para origem `127/8` saindo por outra interface que não o `lo`, senão o destino recebe um pacote com origem `127.0.0.1` e o descarta.

Assim nada roda dentro da distro, não há processo para cair, e vale para qualquer protocolo:

- **Kubernetes:** qualquer destino que a distro alcance, o que inclui o cluster de referência pela VPN.
- **Docker da própria distro:** `127.0.0.1:<porta publicada>`.
- **Docker do Windows ou Desktop:** fica de fora, avisado na tela, porque a porta publicada fica no loopback do Windows, que a distro não vê no modo NAT.

O WSL regenera o `/etc/hosts` e as regras somem quando a distro reinicia. O Severino confere o bloco e a cadeia sempre que vê a distro rodando de novo, e regrava o que faltar. Remover as rotas, "Limpar tudo" e o `--cleanup` apagam o bloco e a cadeia.

Como ficou na implementação (passo 6):

- **Quais distros:** Configurações › WSL lista as distros instaladas, cada uma com um botão de ligar. Importar serviços de uma distro já liga aquela distro, e o aviso da importação diz isso.
- **Um script só, como root:** `wsl.exe -d <distro> -u root --exec sh -s`, com o script na entrada padrão. Ele grava o bloco no `/etc/hosts` (mesmos marcadores do Windows, no lugar do anterior ou no fim) e refaz as cadeias `SEVERINO` (DNAT, em `OUTPUT`) e `SEVERINO-POST` (MASQUERADE, em `POSTROUTING`). O valor anterior do `route_localnet` fica em `/run`, e a remoção o devolve.
- **Quando roda:** a cada mudança de config, e quando uma distro marcada aparece rodando. A lista de distros rodando é consultada a cada 10 s, sem tocar nas distros. Uma falha fica na tela até mudar a config, a distro reiniciar ou "Aplicar de novo", para não manter a distro acordada tentando.
- **Pausar e sair** tiram os nomes das distros, como o bloco do Windows. O `--cleanup` passa também pelas distros paradas.
- **Destinos por nome** (o `hostname` de um LoadBalancer) são resolvidos dentro da distro; um que não resolve aparece como falha parcial.

Na importação, as caixas **Kubernetes** e **Docker** escolhem o que perguntar e filtram a lista. Desmarcar o Kubernetes evita esperar o `kubectl` quando o cluster não interessa ou a VPN está desligada. A escolha fica guardada.

## Fora do escopo

- **Fase 6:** `kubectl port-forward` gerenciado pelo Severino, para services só `ClusterIP` e sem gateway.
- **Fase 6:** acompanhar mudanças sozinho (watch do `kubectl`, eventos do Docker). Nesta fase, atualizar é um clique.
- **Sem planos (decidido em 2026-10-10):** containers chamando os serviços pelos nomes do Severino, pelo DNS interno do Docker ou por `extra_hosts`. Só as portas publicadas no host contam.
- **Sem planos:** Podman, Rancher Desktop com `nerdctl` e clusters que exijam VPN aberta pelo próprio Severino.

## Testes

**Unitários**

- leitura das saídas do `kubectl` (services de todos os tipos, Ingress) e do `docker` (`ps --format json` e `inspect`), a partir de exemplos gravados, incluindo o reconhecimento do formato colado;
- geração dos nomes, com as variantes do Kubernetes e o Compose, e os conflitos entre fontes;
- reserva de endereços em `127.77.0.0/16`, estável entre execuções e sem repetir;
- validador: nomes de uma parte aceitos só em rota de serviço, e o Helper recusando endereços fora de `127.0.0.0/8`;
- montagem dos comandos do WSL, com aspas e `WSL_UTF8`, e leitura da lista de distros;
- bloco do `/etc/hosts` da distro, com as mesmas garantias do bloco do Windows.

**Integração**

- encaminhamento TCP: eco de bytes nos dois sentidos, conexões simultâneas, `Host` intacto numa requisição HTTP repassada, fechamento dos dois lados;
- duas rotas na mesma porta, em endereços diferentes, sem se misturar;
- porta já ocupada por outro processo, com o dono informado.

**Checklist manual** (na máquina de referência, contra o cluster de `staging`)

- os critérios de pronto 1, 4, 5, 6, 7 e 8;
- a distro reiniciada mantém o bloco do `/etc/hosts`.

## Ordem de implementação

1. Validações iniciais.
2. Helper e validador: protocolo v2, endereços `127/8` e nomes de uma parte.
3. Encaminhamento TCP e a rota de serviço, cadastrada à mão.
4. Descoberta: o executor de comandos (Windows e WSL), Kubernetes, Docker e o colar.
5. Tela "Importar serviços" e "Atualizar".
6. Chamadores no WSL: o bloco do `/etc/hosts` da distro.
7. Checklist manual e ajustes.

## Pacotes novos

Nenhum. O `kubectl` e o `docker` rodam como processos, e as saídas são lidas com `System.Text.Json`.

## Decisões a confirmar

1. **Encaminhamento TCP puro, com um endereço de loopback por rota de serviço.** *Padrão: sim.* No cluster de referência há Postgres, Redis e Memcached além do HTTP, e o modo NodePort não depende do `Host`. Rotear só HTTP pelo YARP não serviria.
2. **O Helper aceita qualquer endereço em `127.0.0.0/8`.** *Padrão: sim.* Continua só loopback; muda o protocolo do pipe para a versão 2.
3. **Nomes de uma parte só em rotas de serviço.** *Padrão: sim.* No Windows, um nome como `redis` no hosts também vence nomes de máquinas da rede local; o formulário avisa quando o nome responde na rede.
4. **Kubernetes: NodePort e LoadBalancer nesta fase; `port-forward` na próxima.** *Padrão: sim.* O nó padrão é o da API do contexto (`192.168.203.100`).
5. **Hosts dos Ingress podem virar rotas web para o gateway interno.** *Padrão: sim, opcional na importação.* Leva `staging.pagtel.com.br` direto ao `ingress-nginx` pela VPN, sem passar pelo F5. Implementado depois, em 2026-10-10: a importação lê `kubectl get ingress -A`, mostra cada host numa seção própria, e o host marcado vira uma rota web para o controlador (o endereço do status do Ingress, ou a porta 80 do service `*ingress*controller*`), com o `Host` original preservado, porque o Ingress roteia por ele.
6. **Distros paradas não são iniciadas sem pedir.** *Padrão: sim.*
7. **Chamadores no WSL por `/etc/hosts` mais regras de `iptables` na distro.** *Padrão: sim.* Validado no cluster de referência e com o Docker da distro. A alternativa é exigir o modo de rede espelhado do WSL (`networkingMode=mirrored`), que compartilha o loopback com o Windows, mas muda a rede da distro inteira e exige Windows 11 22H2 ou mais novo. O custo do padrão: o Severino liga o `route_localnet` da distro, que deixa pacotes destinados a `127/8` saírem dela. Ele só faz isso enquanto houver rotas de serviço para aquela distro.
8. **Atualizar é manual.** *Padrão: sim.* Acompanhar sozinho fica para a Fase 6.

## Perguntas em aberto

Respondidas pelo cluster de referência e pela conversa de 2026-10-10:

- **O gateway:** `ingress-nginx` em `192.168.203.100`, roteando por caminho num host só. O "IP do gateway na porta do serviço" é a NodePort, no IP do master.
- **Os protocolos:** HTTP na grande maioria, mais Postgres, Redis e Memcached em TCP puro.
- **Os contextos:** um só, `kubernetes-admin@kubernetes`.
- **Containers como chamadores:** fora. Só as portas publicadas no host contam.

Respondidas em 2026-10-10, todas com o padrão:

1. **Nomes:** as quatro variantes por service, curto, com namespace, `.svc` e `.svc.cluster.local`.
2. **Chamadores:** no Windows e no WSL, então as duas partes entram nesta fase.
3. **Importação:** escolher um por um, com busca e filtro, mais um atalho para marcar o namespace inteiro.

O texto original das perguntas:

1. **Como o código chama os serviços?** Pelo nome curto (`algarbffapi`), com namespace (`algarbffapi.staging`) ou pelo nome completo (`algarbffapi.staging.svc.cluster.local`)? O padrão é gerar os três, mais `.svc`. Se o código usa um só, a importação pode gerar só esse e deixar o hosts mais enxuto.
2. **Onde roda o app que chama os serviços:** no Windows, no WSL, ou nos dois? Isso decide se a parte do WSL é obrigatória já nesta fase ou se pode vir depois.
3. **Importar tudo ou escolher?** Com 119 services, importar todos encheria o hosts de nomes que você não usa. O padrão é você marcar quais quer, com busca e filtro por namespace. Prefere um "importar o namespace inteiro"?
