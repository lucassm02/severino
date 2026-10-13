# Ideias guardadas

Ideias discutidas e deixadas para depois, sem decisão tomada. Nenhuma tem spec ainda. Quando uma for escolhida, ganha spec em [specs/](specs/) e sai daqui.

## WSL com HTTPS, regras e segurança

Registrado em 2026-10-13. Hoje, na distro, o iptables leva o app direto ao destino do serviço, sem passar pelo Windows. Por isso as portas HTTP dos serviços não têm HTTPS do Severino, regras nem segurança dentro do WSL, e as rotas (`meuapp.sev`) nem existem lá. Antes de escolher, falta saber dois pontos: onde os apps costumam rodar (Windows, WSL ou cluster) e qual modo de rede o WSL usa.

### 1. Detectar o modo de rede

Barato, e vale para qualquer caminho.

- O Severino lê o `networkingMode` do `.wslconfig`.
- **Espelhado:** a distro enxerga o `127.0.0.1` do Windows. O iptables manda as portas HTTP para o proxy do Windows, com HTTPS na 443. HTTPS em outras portas fica de fora, porque o iptables não lê o primeiro byte.
- **NAT:** Configurações › WSL explica o limite e como passar para o espelhado.

### 2. Ponte pelo interop do WSL

- Um ouvinte na distro chama, a cada conexão, um `.exe` pequeno do Severino. Rodando no Windows, ele conecta no `127.0.0.1` do proxy. É o mesmo truque do `npiperelay`.
- Não escuta em nenhuma placa de rede e não pede firewall.
- Custa abrir um processo por conexão, entre 50 e 100 ms.

### 3. Ponte por Hyper-V socket

- Windows e VM do WSL conversam por hvsocket (`AF_VSOCK` no Linux), sem placa de rede. É assim que o próprio WSL encaminha o localhost.
- O Severino escuta num hvsocket, e um repassador na distro liga `127.77.x.y:porta` a ele.
- Não tem custo por conexão nem pede firewall.
- Exige registrar uma vez um GUID de serviço no registro, como administrador, pelo Helper. Na distro, precisa do `socat` ou de um binário do Severino.
- Pede prova de conceito.

### 4. Escutar na placa virtual do WSL

A que mais se afasta do plano.

- Muda a decisão fixa de escutar só no loopback.
- Pede regra de firewall restrita à placa.
- O IP da placa muda a cada boot no modo NAT.

### 5. Agente do Severino dentro da distro

Cada sistema tem o seu porteiro, e nenhum atravessa para o outro.

- **Distribuição:** um binário Linux sai junto do instalador do Windows. O Severino o copia para `/opt/severino/` em cada distro escolhida e o troca quando a versão muda.
- **Canal:** o Severino inicia o agente com `wsl.exe -d <distro> -u root -- /opt/severino/agent`.
  - A configuração desce pela entrada padrão; o log de requisições e a saúde sobem pela saída.
  - Fechar o Severino fecha o canal e encerra o agente.
- **Na distro:** o agente escuta só no loopback dela.
  - Serviços em `127.77.x.y`, no lugar do iptables.
  - Rotas num endereço próprio, como `127.77.0.1:80/443`.
  - Um bloco no `/etc/hosts`.
  - HTTPS, regras e segurança com o mesmo código do Windows.
- **Certificados:** a chave da CA não sai do Windows. O Windows emite os certificados de cada nome e os manda pelo canal, gravados como root com permissão 600.
- **Custos:**
  - Separar o proxy do que é só Windows (DPAPI, repositório de certificados, lista de portas). É o maior trabalho.
  - Cerca de 40 MB por distro: o YARP não é oficialmente compatível com AOT.
  - A distro fica acordada enquanto o agente roda. Por isso, só iniciar o agente numa distro já de pé.
  - No modo espelhado, Windows e WSL dividem as portas do loopback. Ali o agente fica desligado e vale a ideia 1.
  - Apps no localhost do Windows continuam inalcançáveis no modo NAT, como hoje.

### Para qualquer ponte ou agente

- **Rotas no WSL.** `meuapp.sev` abre de dentro da distro, com HTTPS, regras e segurança.
- **Escolha por serviço.** Uma opção "no WSL: direto ou pelo Windows". Direto é mais rápido; pelo Windows traz HTTPS, regras, segurança e Requisições.

### A mais promissora

O agente na distro (5), junto com a detecção do modo de rede (1): no modo espelhado, o proxy do Windows atende; no modo NAT, o agente. O desenho completo fica "espelhado usa o Windows, NAT usa o agente".

- **Funciona no NAT, o padrão,** sem firewall, sem registro, sem administrador e sem escutar fora do loopback. A decisão de escutar só no loopback continua de pé: cada sistema escuta no seu.
- **O canal já está provado.** O Severino já fala com as distros pelo `wsl.exe` para os scripts de iptables e para a CA no WSL. Manter um processo com entrada e saída abertas é um passo curto a partir disso.
- **Nenhum salto a mais.** Com o app no WSL, a requisição vai direto do agente ao destino; qualquer ponte iria ao Windows e voltaria.
- **Traz as rotas para o WSL**, e não só os serviços.
- **A ponte por hvsocket (3) também leva um binário à distro,** o repassador. Já que vai haver um binário de qualquer jeito, o agente aproveita melhor esse custo.
- **Ordem:** primeiro a detecção e o modo espelhado (1), que são baratos; depois o agente.

**O que pesa:**
- A refatoração do proxy para rodar também no Linux, que separa de vez o que é comum do que é só Windows.
- Uns 40 MB por distro.
- Certificados de cada nome gravados na distro; a chave da CA não sai do Windows.

**Quando a escolha muda:** se os apps rodam no Windows e não no WSL. No NAT, o agente não alcança o localhost do Windows, e aí a ponte por hvsocket (3) ganha, porque leva a distro até o proxy do Windows. A ponte pelo interop (2) fica como reserva.

## Segurança

- **Bloquear sites externos.** Recusar (403) requisições disparadas por páginas da internet, por `Sec-Fetch-Site: cross-site` vindo de uma origem fora do Severino. Seria uma opção na seção Segurança da rota.
- **Só o meu usuário do Windows.** Recusar conexões de processos de outro usuário da máquina, como num servidor RDP ou numa VM compartilhada. O dono da conexão sai de `GetExtendedTcpTable`.
- **Segurança nas portas HTTP dos serviços.** Hoje a seção Segurança só existe nas rotas.
- **Avisar quando o app escuta em `0.0.0.0`.** O app fica aberto à rede local.
- **TLD inventado que passou a existir.** Consultar de tempos em tempos a lista da IANA e pedir a reemissão da CA restrita aos nomes exatos.
- **CA com validade menor.** Hoje são 10 anos; trocar por 1 ou 2, com troca em um clique e aviso antes de vencer.
- **Provar a origem do instalador.** Atestação de build no GitHub Actions, que é grátis, ou assinatura de código pelo Azure Trusted Signing ou pelo SignPath.
- **Histórico do Helper.** O que o serviço com permissão de administrador mudou no Windows, e aviso quando outro programa mexe no bloco do Severino no hosts.

## Uso diário

- **Resposta fixa (mock) como regra de rota**, inclusive a partir de uma requisição capturada.
- **Rotas a partir de labels do Docker**, como `severino.host=meuapp.sev`.
- **Sugerir uma rota quando uma porta nova começa a escutar.**
- **CA confiável nas ferramentas do Windows:** Node (`NODE_EXTRA_CA_CERTS`), Python (`REQUESTS_CA_BUNDLE`), Git e Java.
- **Limitar a velocidade da rota**, para simular 3G.
- **Frames de WebSocket** nos detalhes da requisição.
