# Spec: Fase 6, DNS e extras

**Status:** aprovado em 2026-10-10: decisão 1 pela opção C, decisões 2 a 4 no padrão, decisão 5 com a fase inteira; respostas no fim
**Base:** pedido de 2026-10-10: "inclua nela uma aba para apenas gerenciamento de DNS, ou seja, apenas cadastrar/editar/deletar uma resolução de IP. Faça em seguida uma melhor integração com esses DNSs já previamente cadastrados com a parte de rotas e serviços". Junta os extras listados no [planejamento](../planejamento.md), que a pessoa aprovou todos.
**Pré-requisito:** a Fase 4 implementada. O checklist dela e a primeira release da Fase 5 ficam para depois, por decisão de 2026-10-10.

## Objetivo

O Severino passa a ser o lugar de todos os nomes da máquina de desenvolvimento, não só dos que passam pelo proxy:

- **DNS simples:** `sql.interno → 10.0.0.8`, cadastrado numa aba própria, como hoje se faz à mão no hosts;
- **integrado:** esse nome vira destino de rotas e de serviços, e mudar o IP num lugar só atualiza tudo que depende dele;
- **e os extras:** curinga (`*.callfred.sev`), rotas por caminho, grupos, `kubectl port-forward`, acompanhar o cluster sozinho e um módulo PowerShell.

## O que a máquina de referência mostrou

Consultas só de leitura, em 2026-10-10:

- **O hosts tem 20 entradas feitas à mão, fora do bloco do Severino, todas com IP de rede** (nenhuma de loopback). É a infraestrutura interna que a pessoa acessa pela VPN. A aba DNS substitui essa edição manual, e por isso precisa aceitar IPs que não são de loopback.
- **O WSL já enxerga o hosts do Windows.** A distro usa o túnel de DNS do WSL (`nameserver 10.255.255.254`, WSL 3.0.1). Um nome do hosts do Windows resolveu para o mesmo IP dentro da distro, sem estar no `/etc/hosts` dela. Entradas DNS com IP de rede, portanto, valem no WSL sem trabalho extra. As de loopback não, como já se sabia (por isso o `iptables` da Fase 4).
- **A porta 53 está ocupada** pelo serviço ICS, Compartilhamento de Conexão com a Internet (`svchost`, UDP `0.0.0.0:53`). Afeta o curinga, que precisa de um DNS em `127.0.0.1:53`.
- **Nenhuma regra NRPT ativa**, com a VPN desligada.

## Critérios de pronto

1. **Aba DNS.** Cadastrar, editar, ligar e desligar e remover uma entrada `nome → IP`, com IPv4 ou IPv6 de qualquer faixa. O nome resolve no Windows e no WSL.
2. **Importar o hosts.** As 20 entradas feitas à mão entram na aba DNS em poucos cliques, e o hosts não fica com duas versões do mesmo nome.
3. **Persistência.** As entradas DNS continuam valendo com o Severino fechado ou pausado, como entradas escritas à mão. "Limpar tudo" e o desinstalador devolvem o hosts como era antes.
4. **Nomes como destino.** Uma rota `api.callfred.sev → http://gateway.k8s:8080` e um serviço `postgres → gateway.k8s:30711` funcionam. Mudar o IP de `gateway.k8s` na aba DNS muda o destino dos dois, sem editá-los.
5. **Uma lista só de nomes.** Um nome é de uma entrada DNS, de uma rota ou de um serviço, nunca de dois. O app avisa também quando um nome novo já está no hosts fora do Severino.
6. **Os extras**, cada um com o próprio critério na seção dele.
7. **Testes.** `dotnet test` passa e o build segue sem avisos.

## Aba DNS

### A entrada

- **Nomes:** um ou mais, como numa linha do hosts (`10.0.0.5 api.interno api`). Valem nomes de uma parte só, como nas rotas de serviço.
- **Endereço:** IPv4 ou IPv6, de qualquer faixa: loopback, rede privada ou pública.
- **Ligada** e **Notas**, como nas rotas.

A aba fica entre Rotas e Requisições. Mostra a lista com busca, o estado de cada entrada ("valendo", "desligada", "aguardando aplicar") e, para cada uma, quem a usa ("destino de 3 serviços").

### Onde a entrada é gravada

As entradas DNS ganham um **segundo bloco no hosts**, separado do bloco das rotas:

| | Bloco das rotas e serviços (hoje) | Bloco DNS (novo) |
|---|---|---|
| Conteúdo | nomes do proxy e dos serviços, sempre loopback | `nome → IP` de qualquer faixa |
| Quem grava | o Helper, pelo pipe, sem pedir nada | depende da decisão 1 |
| App fechado ou pausado | esvazia | continua valendo |
| "Limpar tudo" e desinstalar | some | some, e as linhas importadas voltam |

O bloco DNS persiste porque substitui edições à mão: quem cadastra `sql.interno` espera que ele funcione com o app fechado, como funcionava antes.

### Segurança: quem pode gravar IP de rede no hosts

Hoje o pior que outro programa da conta consegue pelo pipe é mandar um nome para a própria máquina. Aceitar qualquer IP pelo mesmo caminho muda isso: qualquer programa rodando como você poderia mandar `banco.com.br` para um servidor de fora, sem confirmação, e esse é o golpe clássico do hosts. Há três jeitos de fazer (decisão 1):

- **A, recomendado: confirmação do Windows a cada aplicação.** A aba acumula as mudanças, e **Aplicar** grava o bloco DNS por um processo elevado, com o aviso do UAC. O Helper continua só com loopback, e a decisão fixa do projeto não muda. Custo: um UAC a cada aplicação (importar as 20 entradas é uma aplicação só).
- **B: o Helper aceita qualquer IP.** Sem UAC, mas com a brecha descrita acima. Exige mudar a decisão fixa no `CLAUDE.md` e no planejamento.
- **C: o Helper aceita faixas privadas** (`10/8`, `172.16/12`, `192.168/16`, `100.64/10`, `fc00::/7`) **sem UAC; IP público pede UAC.** Cobre a infraestrutura interna sem confirmação, mas uma rede privada atacável continua possível, e são dois caminhos de escrita para manter.

### Importar o hosts

- A aba mostra quantas linhas do hosts estão fora do Severino, com **Importar**: uma lista com caixas de seleção, já agrupando nomes de mesmo IP numa entrada.
- Importar cria as entradas e **comenta as linhas originais** no hosts, com um marcador do Severino (`# [severino] 10.0.0.8 sql.interno`), para não haver duas versões do mesmo nome. Desinstalar ou "Limpar tudo" descomenta essas linhas: o hosts volta a ser o de antes (decisão 3).

## Integração com rotas e serviços

1. **Nome como destino.** Nos formulários de rota e de serviço, o campo do host de destino sugere as entradas DNS ("gateway.k8s, 192.168.203.100"). O destino guarda o nome, e quem resolve é o Windows, pelo hosts. Mudar o IP na aba DNS muda o destino de todos, e o Severino limpa o cache de DNS ao aplicar.
2. **Nó das NodePorts por nome.** Na importação de serviços, se uma entrada DNS aponta para o IP do nó, ela aparece como opção ("gateway.k8s, 192.168.203.100"). Os serviços importados guardam o nome, e o "Atualizar" mantém o nome.
3. **"Usado por".** Cada entrada DNS mostra quantas rotas e serviços dependem dela. Remover uma entrada em uso pede confirmação e lista quem para de funcionar.
4. **Uma lista só de nomes.** A validação de rotas, serviços e DNS olha as três listas e também as linhas do hosts fora do Severino ("sql.interno já está no hosts, fora do Severino. Importe para a aba DNS ou escolha outro nome").
5. **Converter.** Uma entrada DNS vira rota com um clique: `api.interno → 10.0.0.5` vira `api.interno → http://10.0.0.5:<porta>`, ganhando HTTPS da CA local e o log de requisições. O caminho contrário também existe: "Transformar em DNS simples" numa rota cujo destino é um IP fixo.
6. **No WSL:** as entradas DNS de IP de rede já chegam pelo túnel de DNS. Os destinos por nome das regras de `iptables` são resolvidos na distro pelo `getent`, que também passa pelo túnel; nada muda no script da Fase 4.

## Os extras

Cada um com o seu critério de pronto, na ordem de implementação.

### Curinga por DNS embutido e NRPT

- **O que é:** uma rota `*.callfred.sev → localhost:3000` (e uma entrada DNS `*.dev.interno → 10.0.0.5`) vale para qualquer subdomínio. O hosts não aceita curinga, então o Severino responde esses nomes com um DNS próprio em `127.0.0.1:53`. Uma regra NRPT do Windows manda só os sufixos curinga para ele; todo o resto segue para o DNS normal e o da VPN.
- **Critério:** `curl https://cliente42.callfred.sev` abre o app, com certificado curinga, sem cadastrar `cliente42`.
- **Validar antes:** se dá para escutar em `127.0.0.1:53` com o ICS em `0.0.0.0:53`; se a VPN, ligada, instala regras NRPT que passam na frente; se o WSL (túnel de DNS) respeita a NRPT; como Chrome, Edge e Firefox com DNS seguro se comportam. Se a porta 53 não der, o curinga fica de fora da fase, registrado aqui.

### Rotas por caminho

- **O que é:** `callfred.sev/api → localhost:8080` e `callfred.sev → localhost:3000` no mesmo domínio, com a opção de tirar o prefixo antes de repassar.
- **Critério:** as duas respondem pelo mesmo `https://callfred.sev`, cada uma no seu app.

### Grupos de rotas

- **O que é:** um rótulo por rota ("callfred", "loja"), para ligar, desligar e filtrar o grupo inteiro. Os serviços continuam agrupados pela origem.
- **Critério:** desligar o grupo "callfred" tira todas as rotas dele do hosts de uma vez.

### `kubectl port-forward` gerenciado

- **O que é:** para services só ClusterIP, que hoje aparecem como "sem acesso de fora", o Severino mantém um `kubectl port-forward` rodando (no Windows ou no WSL, onde o `kubectl` estiver) e aponta o serviço para ele. Reinicia se cair, e para quando o serviço é desligado.
- **Critério:** um service ClusterIP importado responde pelo nome, e volta a responder sozinho depois de a VPN cair e voltar.

### Acompanhar mudanças sozinho

- **O que é:** o "Atualizar" da Fase 4, automático, com `kubectl get --watch` e `docker events`, enquanto o app está aberto.
- **Critério:** recriar um container com outra porta publicada corrige a rota em segundos, sem clique.

### Módulo PowerShell

- **O que é:** `Get-SeverinoRoute`, `New-SeverinoRoute`, `Get-SeverinoDns`, `Set-SeverinoDns` e afins, falando com o app aberto por um pipe da própria conta, com as mesmas validações da interface.
- **Critério:** um script cria uma rota e uma entrada DNS, e as duas aparecem no app na hora.

## Testes

**Unitários:** o bloco DNS (mesmas garantias do bloco atual, convivendo com ele), a importação das linhas do hosts e o comentário com marcador e a volta dele, a validação cruzada de nomes nas três listas, a resolução de destino por nome, a contagem de "usado por", a conversão DNS em rota e o caminho de volta, rotas por caminho no YARP, e os grupos.

**Integração:** o processo elevado de escrita (sem UAC, num hosts de teste), o DNS embutido respondendo curinga, e o `port-forward` com um `kubectl` falso.

**Checklist manual:** os critérios de pronto e o de cada extra, mais desinstalar devolvendo as 20 linhas originais ao hosts.

## Ordem de implementação

1. Validações iniciais (as do curinga, e o UAC da decisão 1).
2. Aba DNS e o bloco DNS persistente.
3. Importar o hosts.
4. Integração: nome como destino, nó por nome, "usado por", validação cruzada e converter.
5. Curinga, se as validações deixarem.
6. Rotas por caminho.
7. Grupos de rotas.
8. `kubectl port-forward` gerenciado.
9. Acompanhar mudanças sozinho.
10. Módulo PowerShell.
11. Checklist manual.

## Decisões

Respondidas em 2026-10-10:

1. **Quem grava IP de rede no hosts: opção C.** O Helper grava o bloco DNS com loopback e faixas privadas (`10/8`, `172.16/12`, `192.168/16`, `100.64/10`, `169.254/16`, `fc00::/7`, `fe80::/10`) sem confirmação. Um IP público só entra depois de aprovado por um processo elevado, com o aviso do UAC; a aprovação de cada par `nome → IP` fica guardada no `HKLM`, que só administrador escreve, e o Helper confere essa lista. A decisão fixa do `CLAUDE.md` passa a dizer isso.
2. **Entradas DNS persistem com o app fechado ou pausado.** Pausar continua tirando só rotas e serviços.
3. **Importar comenta as linhas originais, e desinstalar ou "Limpar tudo" as devolve.**
4. **Destinos guardam o nome, não o IP.**
5. **A fase inteira de uma vez**, nos 11 passos da ordem de implementação. O site da Fase 5 é complementado no fim.

## Perguntas respondidas

1. **As entradas do hosts:** algumas não são da pessoa (podem ser de outros programas). A importação só oferece as linhas; nenhuma vem marcada, e linhas dentro de blocos de outras ferramentas (como o do Docker Desktop) aparecem com a origem.
2. **IP público:** coberto pela opção C, com UAC.
