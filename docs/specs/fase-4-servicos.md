# Spec: Fase 4, Serviços (Kubernetes, Docker e WSL)

**Status:** rascunho, aguardando as "Decisões a confirmar" e as respostas das "Perguntas em aberto"
**Base:** pedido de 2026-10-09: "o meu serviço local chama direto o Kubernetes, pelo IP do entry point do gateway, na porta do serviço, mas com o nome correto", estendido para Docker e WSL. É uma fase nova, que diverge do [planejamento](../planejamento.md): ele deixava containers e WSL fora do escopo. A divergência e a mudança do roadmap estão registradas lá. Segue o formato dos specs anteriores.
**Pré-requisito:** a Fase 3 concluída.

## Objetivo

Um app rodando na máquina, no Windows ou numa distro do WSL, chama um serviço pelo mesmo nome e porta que usaria dentro do cluster ou da rede do Compose, e chega ao destino certo, sem mudar a configuração do app:

- `http://pedidos:8080` ou `http://pedidos.staging.svc.cluster.local:8080` chegam ao gateway do cluster, na porta do serviço, com o nome original na requisição;
- `db:5432`, o nome do serviço no Compose, chega à porta publicada do container.

Os serviços e containers são descobertos pelo próprio Severino, rodando `kubectl` e `docker` no Windows ou dentro das distros do WSL. Quando a descoberta automática não servir, dá para colar a saída dos comandos.

## O cenário de referência

Levantado na máquina de desenvolvimento, em 2026-10-09:

- **Windows:** não tem `kubectl` nem `docker`.
- **Distro Ubuntu-22.04 no WSL2:**
  - tem `kubectl` em `/usr/local/bin` e Docker Engine 29 nativo, sem Docker Desktop;
  - o contexto atual do `kubectl` é `kubernetes-admin@kubernetes`, com namespace `staging`.
- **Rede do WSL:** modo NAT, o padrão, com `localhostForwarding=true`. Dentro da distro, `127.0.0.1` é o loopback do WSL, não o do Windows.
- **PATH:** o `docker` só aparece com um shell de login (`bash -lc`). Um `--exec` direto não carrega o PATH completo.
- **Servidor de dev:** o da rota `callfred.sev` é o container `callfred-orchestrator` (serviço `orchestrator` do Compose `callfred`), no Docker da distro, publicado em 24600 e repassado ao Windows pelo `wslrelay`. Então quem chama os serviços do cluster pode estar no WSL ou num container, e não só no Windows.

## Critérios de pronto

1. **Descoberta no WSL.** Com a distro rodando, "Importar serviços" lista os services do contexto atual do `kubectl` da distro e os containers do Docker da distro, sem nenhum comando digitado.
2. **Descoberta no Windows.** Com `kubectl` ou `docker` no Windows, eles aparecem como outra fonte. O Docker Desktop, que é o mesmo engine visto do Windows e das distros, aparece uma vez só.
3. **Colar.** Colar a saída de `kubectl get svc -A -o json` ou de `docker ps --format json` produz a mesma lista da descoberta automática.
4. **Kubernetes pelo gateway.** No Windows, depois de importar `pedidos` (namespace `staging`, porta 8080) com o gateway informado:
   - `curl http://pedidos:8080/health` e `curl http://pedidos.staging.svc.cluster.local:8080/health` respondem como dentro do cluster;
   - o gateway recebe o nome usado na chamada, sem troca.
5. **Docker pelo nome do Compose.** Com o serviço `db` do Compose publicando 5432 na porta 15432, `psql -h db -p 5432` no Windows conecta.
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
- **Docker:** Docker Engine 29 na distro, sem Docker Desktop, com um container: `callfred-orchestrator`, do Compose `callfred`, serviço `orchestrator`, publicando `0.0.0.0:24600 → 4000`. É o destino da rota `callfred.sev` de hoje, que chega ao Windows pelo `wslrelay`. O `docker ps --format json` já traz os rótulos do Compose. O `docker inspect` só é preciso para o IP do container, no caso de chamadores dentro do WSL.
- **`/etc/hosts` da distro:** é gerado pelo WSL (`generateHosts` ligado, o padrão), então um bloco gravado ali some quando a distro reinicia. O Severino precisa notar o reinício e regravar: ele confere o bloco quando a distro aparece rodando de novo.
- **Root na distro:** `wsl -u root` funciona sem senha, então o bloco do `/etc/hosts` dispensa qualquer pedido no Windows.
- **Rede da distro:** modo NAT; o Windows, visto de dentro da distro, é `172.24.16.1`.
- **Pendentes, por falta de acesso ao cluster:** o alcance do gateway a partir do WSL e o repasse TCP com o `Host` original. Ficam para quando a VPN estiver conectada.

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

- **Comandos:** `kubectl config get-contexts -o name` lista os contextos, e `kubectl get svc -A -o json --context <ctx>` lista os services.
- **Ingress (opcional):** `kubectl get ingress -A -o json` acrescenta os hosts dos Ingress. Eles viram rotas web apontando para o gateway, porque já são nomes HTTP de verdade.
- **Destino, por contexto, escolhido na importação:**
  - **Gateway**, o seu caso: o IP ou nome do entry point, informado uma vez por contexto. O destino é `gateway:porta-do-serviço`.
  - **LoadBalancer:** o IP em `status.loadBalancer.ingress`, na porta do serviço.
  - **NodePort:** o IP de um nó e o `nodePort`.
  - Services só `ClusterIP` aparecem como "sem acesso de fora", com a dica do modo Gateway.

**Docker:**

- **Comandos:** `docker ps --format json` e `docker inspect` dos containers rodando, para as portas e os rótulos do Compose (`com.docker.compose.service`, `com.docker.compose.project`).
- **Só portas publicadas.** O destino é a porta publicada:
  - com o Docker no Windows ou no Docker Desktop, `127.0.0.1:<porta publicada>`;
  - com o Docker numa distro, a mesma porta, que chega ao Windows pelo `localhostForwarding`.
  
  Portas não publicadas aparecem como "não publicada", com a dica de publicar.

**Colar:** um campo aceita a saída de `kubectl get svc -A -o json`, de `kubectl get ingress -A -o json`, de `docker ps --format json` ou de `docker inspect`, e reconhece o formato sozinho.

**Tela "Importar serviços":**

- escolhe a fonte;
- lista os services e containers com caixas de seleção, mostrando os nomes que serão criados, as portas e o destino;
- avisa conflitos com rotas existentes;
- cria as rotas agrupadas pela origem.

"Atualizar" refaz a descoberta da origem e corrige destinos e portas, sem mexer em nomes que a pessoa editou.

### Chamadores no WSL

Um app dentro da distro não enxerga o loopback do Windows no modo NAT, então o hosts do Windows não serve para ele. Para as distros escolhidas, o Severino mantém um bloco gerenciado no `/etc/hosts` da distro, escrito como root pelo `wsl -u root`, sem pedir administrador no Windows. Cada nome aponta para o endereço que funciona de dentro da distro:

- **Kubernetes pelo gateway:** o IP do gateway, que a distro alcança direto. A porta do serviço é a mesma do gateway, então não é preciso proxy.
- **Docker da própria distro:** o IP do container na rede do Docker, na porta do container. Também dispensa proxy.
- **Docker do Windows ou Desktop, e LoadBalancer ou NodePort com porta diferente:** sem caminho direto. Ficam de fora desta fase, avisados na tela, ou dependem do modo de rede espelhado (ver "Decisões a confirmar").

O bloco usa os mesmos marcadores do hosts do Windows e é regravado quando a distro reinicia, se a validação mostrar que o WSL o apaga.

## Fora do escopo

- **Fase 5:** `kubectl port-forward` gerenciado pelo Severino, para services só `ClusterIP` e sem gateway.
- **Fase 5:** acompanhar mudanças sozinho (watch do `kubectl`, eventos do Docker). Nesta fase, atualizar é um clique.
- **Fase 5:** containers chamando o host pelos nomes do Severino.
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

1. **Encaminhamento TCP puro, com um endereço de loopback por rota de serviço.** *Padrão: sim.* A alternativa, só HTTP pelo YARP, roteando por nome, não serviria a bancos e filas, e exigiria reescrever o `Host`.
2. **O Helper aceita qualquer endereço em `127.0.0.0/8`.** *Padrão: sim.* Continua só loopback; muda o protocolo do pipe para a versão 2.
3. **Nomes de uma parte só em rotas de serviço.** *Padrão: sim.* No Windows, um nome como `db` no hosts também vence nomes de máquinas da rede local; o formulário avisa quando o nome responde na rede.
4. **Kubernetes: Gateway, LoadBalancer e NodePort nesta fase; `port-forward` na próxima.** *Padrão: sim.*
5. **Docker: só portas publicadas.** *Padrão: sim.* Containers sem porta publicada não são alcançáveis do Windows sem mais infraestrutura.
6. **Distros paradas não são iniciadas sem pedir.** *Padrão: sim.*
7. **Chamadores no WSL pelo `/etc/hosts` da distro, apontando direto para o destino.** *Padrão: sim.* A alternativa é exigir o modo de rede espelhado do WSL (`networkingMode=mirrored`), que compartilha o loopback com o Windows. É mais simples para o Severino, mas muda a rede da distro inteira e exige Windows 11 22H2 ou mais novo.
8. **Atualizar é manual.** *Padrão: sim.* Acompanhar sozinho fica para a Fase 5.

## Perguntas em aberto

Estas não têm padrão, porque dependem do seu ambiente:

1. **Que gateway é?** Ingress NGINX, Istio, Envoy Gateway, Traefik, ou outro? Ele roteia pelo `Host`, pela porta, ou pelos dois? O IP do entry point é fixo?
2. **Que protocolos os serviços usam?** Só HTTP e gRPC, ou também TCP puro (banco, fila, cache)?
3. **Como o código chama os serviços?** Pelo nome curto (`pedidos`), com namespace (`pedidos.staging`) ou pelo nome completo (`.svc.cluster.local`)?
4. **Um cluster só, ou vários contextos?** Hoje a distro tem um contexto, `kubernetes-admin@kubernetes`.
5. **Onde roda o app que chama os serviços:** no Windows, no WSL, ou nos dois?
