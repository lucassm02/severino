# Spec: Fase 1, MVP HTTP

**Status:** aprovado em 2026-10-09, com os padrões das "Decisões a confirmar"
**Base:** [planejamento.md](../planejamento.md), seções 1, 3, 4 e 7. Este spec detalha a Fase 1 e não muda o plano. Divergências ficam em "Decisões a confirmar".

## Objetivo

Cadastrar `callfred.sev → 127.0.0.1:3000` na janela e abrir `http://callfred.sev` no navegador, com o HMR do Vite funcionando. Sem terminal e sem editar arquivo à mão, depois que o serviço auxiliar estiver instalado.

## Critérios de pronto

Cada item é verificável e binário. A fase só fecha com todos marcados.

1. **Fluxo principal.** Com o Helper instalado, criar a rota `callfred.sev → http://127.0.0.1:3000` grava o bloco no hosts e ativa a rota no proxy em até 1 s, e `http://callfred.sev` abre o app de destino no Edge e no Chrome.
2. **HMR.** Um projeto Vite recém-criado, acessado por `http://callfred.sev`, recarrega o módulo ao salvar um arquivo, sem erro de WebSocket no console.
3. **Ciclo de vida da rota.** Editar, desligar e remover uma rota refletem no hosts e no proxy em até 1 s. "Desfazer" restaura a rota removida. "Sair" pela bandeja remove o bloco do hosts; ao abrir o app de novo, o bloco volta.
4. **Páginas de erro.** Host sem rota recebe 404 com a lista de rotas ativas. Destino fora do ar recebe 502 com o texto `callfred.sev → 127.0.0.1:3000 não respondeu. Seu servidor está rodando?`.
5. **Porta ocupada.** Com outro processo escutando na 80, o app abre normalmente, mostra o dono da porta (nome e PID, com a explicação do http.sys quando for PID 4) e permite trocar a porta HTTP em Configurações, sem reiniciar o app.
6. **Helper blindado.** Mensagens inválidas enviadas direto ao pipe, por um cliente de teste, são rejeitadas sem alterar o hosts: nome com quebra de linha, nome inválido, IP fora do loopback e excesso de entradas. A ACL do pipe só concede acesso ao SYSTEM e ao SID configurado.
7. **Avisos de domínio.** `example.com` mostra o aviso de "existe na internet" mesmo com uma rota ativa para ele; `callfred.sev` não mostra. `app.dev` mostra o aviso de HSTS preload. `algo.local` mostra o aviso de mDNS.
8. **Saúde.** A bolinha de status de cada rota passa de ◐ para ● em até 5 s depois de o destino subir, e volta para ◐ depois que ele cai.
9. **Testes automatizados.** `dotnet test` passa com os testes listados em "Testes", e o build segue sem avisos.

## Escopo

### Projetos novos

| Projeto | Alvo | Conteúdo |
|---|---|---|
| `Severino.Contracts` | `net10.0` | Validador e normalizador de domínio, mensagens do pipe, nome do pipe, versão do protocolo |
| `Severino.Helper` | `net10.0-windows`, Worker | Servidor do pipe, mesclagem do hosts, flush de DNS. Depende só de `Contracts` |
| `Severino.Proxy` | `net10.0`, com `FrameworkReference Microsoft.AspNetCore.App` | Kestrel, YARP, páginas de erro, verificador de saúde |

`Severino.Core` passa a referenciar `Contracts` e ganha o cliente do pipe, o serviço de rotas, a consulta de DNS e o dono de porta. `Severino.App` referencia `Proxy`.

### Validador de domínio (`Contracts`)

Uma única implementação, usada pela UI e pelo Helper.

- Remove espaços nas pontas e o ponto final, e converte para minúsculas.
- Converte acentos para punycode com `IdnMapping`.
- Exige no mínimo dois rótulos. Cada rótulo tem de 1 a 63 caracteres em `[a-z0-9-]`, sem hífen no início ou no fim. O nome inteiro tem no máximo 253 caracteres.
- Rejeita `localhost` exato (subdomínios de `.localhost` são aceitos) e nomes que parecem IP, com o último rótulo só de dígitos.
- Devolve o nome normalizado ou um erro com motivo legível em português.

### Protocolo do pipe (`Contracts`)

- **Pipe:** `\\.\pipe\Severino.Helper`, uma mensagem JSON por linha, UTF-8, com no máximo 1 MB por mensagem.
- **`ping`:** devolve a versão do Helper e a do protocolo. O app avisa quando a versão do protocolo não bate.
- **`sync`:** recebe a lista completa de domínios ativos e devolve `ok` ou um erro. É idempotente: o Helper reescreve o bloco inteiro, sem operações de adicionar e remover. Lista vazia remove o bloco.
- **Limite:** 500 domínios, o que dá 1000 linhas no bloco.

### Helper

- **Conta:** roda como LocalSystem. É a menor conta que consegue gravar o hosts sem mexer na ACL dele.
- **ACL do pipe:** acesso total para o SYSTEM, leitura e gravação para um único SID e nada mais. O SID vem de `HKLM\SOFTWARE\Severino\Helper`, valor `AllowedUserSid`, que só o administrador consegue gravar. O instalador da Fase 3 e o script de desenvolvimento gravam esse valor. O pipe é criado com `FirstPipeInstance`, para que nenhum processo o crie antes do Helper.
- **Mesclagem do hosts:** função pura, `HostsBlock.Merge(textoAtual, domínios) → textoNovo`, coberta por testes. Segue a seção 4 do plano:
  - substitui só o bloco e preserva o resto byte a byte;
  - grava num arquivo temporário e troca com `File.Replace`, gerando `hosts.severino.bak`;
  - remove e restaura o atributo somente-leitura;
  - grava em ASCII sem BOM e com CRLF;
  - chama `DnsFlushResolverCache`.
  
  Um bloco corrompido, com início e sem fim, é tratado como se fosse até o fim do arquivo e reescrito. Sem bloco existente, o novo vai no final.
- **Revalidação:** cada domínio recebido passa de novo pelo validador. Se algum falhar, a mensagem inteira é rejeitada e o hosts não muda.
- **Execução dupla:** dentro do serviço, `UseWindowsService`; num terminal elevado, sobe como console, para depurar.

### Instalação em desenvolvimento

`scripts/dev-helper.ps1` exige terminal elevado e aceita três ações:

- **`install`:** publica o Helper em `C:\Program Files\Severino\Helper\`, grava `AllowedUserSid` com o SID de quem rodou o script e registra e inicia o serviço `Severino.Helper`. O binário fica numa pasta em que só o administrador grava, porque um executável do SYSTEM numa pasta gravável pelo usuário abriria caminho para elevação de privilégio.
- **`uninstall`:** pede ao serviço para limpar o bloco do hosts, para e remove o serviço, a pasta e a chave do registro.
- **`run`:** roda o Helper como console, a partir do build local, para depurar sem instalar.

### Proxy

- **Endereços:** Kestrel em `127.0.0.1` e `[::1]`, na porta `settings.httpPort`. Só HTTP nesta fase.
- **Rotas:** cada rota ativa vira uma `RouteConfig` com `Match.Hosts = [domínio]` e um cluster com um destino. As mudanças passam por `InMemoryConfigProvider.Update`.
- **Ajustes de dev:**
  - `ActivityTimeout` de 10 minutos;
  - `MaxRequestBodySize = null`;
  - Host do destino por padrão, com `X-Forwarded-Host/Proto/For`;
  - "Preservar Host original" como opção da rota;
  - "Ignorar certificado inválido do destino" vale para destinos `https://`.
- **Proteção contra loop:** destino em loopback, `localhost` ou o próprio domínio da rota, na porta do proxy, é recusado pelo formulário e pelo proxy.
- **Páginas de erro:** HTML próprio, sem dependências externas, nos dois temas.
  - 404 para Host sem rota, com links para as rotas ativas;
  - 502 quando o destino recusa ou estoura o tempo de conexão, usando o `IForwarderErrorFeature`.
- **Troca de porta:** para o Kestrel e sobe de novo na porta nova. As rotas não mudam.
- **Porta ocupada:** o bind falha com `AddressInUse`. O proxy fica no estado "parado", com o dono da porta obtido por `GetExtendedTcpTable`, e o app segue funcionando.

### Rotas no app

- **Lista:** bolinha de status, domínio (clicar abre no navegador), destino, interruptor e menu ⋯ com editar, duplicar, copiar URL e remover.
- **Remover:** sem confirmação. Aparece "Desfazer" por 5 s.
- **Formulário:**
  - domínio completo, com o texto de exemplo `ex.: callfred.sev` e a dica sobre `.test` e `.localhost`;
  - destino com esquema (`http` ou `https`), host (padrão `localhost`) e porta. O proxy e o teste de saúde tentam todos os endereços do host em paralelo: no Windows, uma conexão recusada em `::1` leva cerca de 2 s para falhar, e servidores de dev costumam escutar só em `127.0.0.1` ou só em `::1`;
  - "Avançado" com as opções da rota e observações;
  - erro em vermelho para nome inválido ou duplicado, que bloqueia;
  - amarelo para porta sem nada escutando e para os avisos de domínio, que não bloqueiam.
- **Sincronização:**
  - toda mudança de rota agenda uma sincronização com espera de 300 ms;
  - o app sincroniza ao iniciar e envia lista vazia ao sair;
  - se o Helper não responde, a barra inferior mostra "hosts: serviço auxiliar indisponível". O proxy continua funcionando, e a próxima mudança tenta de novo.
- **Barra inferior:** "Proxy ativo :80 · hosts ok", ou o problema. Clicar num problema leva à correção: Configurações para a porta, ou um texto explicando como instalar o Helper.

### Avisos de domínio (`Core`)

- **Existe na internet:** `DnsQuery_W` com `DNS_QUERY_NO_HOSTS_FILE | DNS_QUERY_BYPASS_CACHE`, consultando A, AAAA e CNAME, com timeout de 5 s (em DNS lento, a resposta "não existe" para um nome novo passa de 2 s). Roda enquanto você digita, com espera de 500 ms, e de novo ao salvar.
- **HSTS preload:** lista embutida de TLDs inteiros (`dev`, `app`, `page`, `new`, `day`, `foo` e outros, a partir da lista do Chromium). Nesta fase só há HTTP, então o aviso diz que o domínio só vai abrir quando o HTTPS chegar.
- **`.local`:** aviso fixo sobre o mDNS.

### Saúde dos destinos (`Proxy`)

Conexão TCP a cada 5 s por rota ativa, com timeout de 1 s. O resultado alimenta a bolinha de status. Rotas desligadas não são testadas.

### Configurações

- **Porta HTTP:** campo numérico; mudar aplica na hora, como descrito em "Troca de porta".
- A porta HTTPS fica fora da tela até a Fase 2.

### Logs

Serilog com arquivo rotativo, mantendo 7 dias:

- o app grava em `%LOCALAPPDATA%\Severino\logs\`;
- o Helper grava em `logs\` ao lado do executável, dentro de `Program Files`. Em `ProgramData`, um usuário comum consegue criar arquivos e plantar links para o SYSTEM gravar por ele.

## Fora do escopo

Fica para as fases seguintes, mesmo que pareça barato agora:

- **Fase 2:** HTTPS, CA, certificados, redirecionamento HTTP→HTTPS, porta HTTPS e o aviso de cobertura da CA.
- **Fase 3:**
  - combo de portas com nome do processo;
  - aba Requisições;
  - assistente de primeira execução;
  - detector de proxy do sistema;
  - iniciar com o Windows;
  - importar e exportar;
  - "Limpar tudo";
  - instalador;
  - lista de rotas e "pausar" no menu da bandeja.
- **Fase 4:** curinga e rotas por caminho.

## Testes

**Unitários**

- **`HostsBlock.Merge`:**
  - idempotência, ou seja, aplicar duas vezes dá o mesmo texto;
  - linhas fora do bloco preservadas;
  - CRLF e LF misturados;
  - arquivo sem bloco, arquivo vazio, bloco corrompido sem fim;
  - lista vazia removendo o bloco.
- **Validador:**
  - nomes válidos, maiúsculas, ponto final, punycode (`café.sev → xn--caf-dma.sev`);
  - rótulo de 64 caracteres, nome de 254 caracteres, hífen nas pontas;
  - `localhost`, nome com cara de IP, nome de um rótulo só;
  - `\r`, `\n`, espaço, `#` e caracteres de controle.
- **Mensagens do pipe:** ida e volta do JSON; mensagem acima de 1 MB e com mais de 500 domínios são rejeitadas.
- **Cálculo da lista sincronizada:** só rotas ativas, sem duplicatas, normalizadas.

**Integração** (Kestrel e YARP em portas aleatórias, com um backend falso)

- roteamento por Host e 404 para Host desconhecido;
- passagem de WebSocket, com eco;
- 502 com o texto esperado quando o destino está fora;
- Host reescrito por padrão e preservado com a opção ligada;
- `X-Forwarded-*` presentes;
- atualização de rotas a quente, sem reiniciar o Kestrel;
- recusa de destino em loop.

**Checklist manual** (no fim da fase)

- os critérios de pronto 1, 2, 3, 5 e 8;
- serviço parado: o app avisa e não trava;
- `scripts/dev-helper.ps1 uninstall` deixa o hosts sem o bloco.

## Ordem de implementação

1. `Contracts`: validador e mensagens, com testes.
2. `Helper`: `HostsBlock`, com testes; servidor do pipe com ACL; flush de DNS; `dev-helper.ps1`.
3. `Core`: cliente do pipe, serviço de rotas com sincronização, consulta de DNS, dono de porta.
4. `Proxy`: host do Kestrel, configuração do YARP, páginas de erro, saúde, com testes de integração.
5. `App`: lista e formulário de rotas, barra inferior, porta em Configurações, limpeza do bloco ao sair.
6. Checklist manual e ajustes.

## Pacotes novos

| Pacote | Versão | Projeto |
|---|---|---|
| `Yarp.ReverseProxy` | 2.3.0 | Proxy |
| `Microsoft.Extensions.Hosting.WindowsServices` | 10.0.12 | Helper |
| `Serilog.Extensions.Hosting` | 10.0.0 | App, Helper |
| `Serilog.Sinks.File` | 7.0.0 | App, Helper |

## Decisões a confirmar

Cada item já tem um padrão adotado neste spec. Basta confirmar ou trocar.

1. **Limpar o bloco do hosts ao sair.** *Padrão: sim.* Com o app fechado, o proxy não responde, e uma rota sobre um domínio real, como `api.empresa.com`, deixaria o site inacessível sem motivo aparente. O custo é uma gravação no hosts a cada abertura e fechamento do app.
2. **Mínimo de dois rótulos no domínio.** *Padrão: sim.* Um nome como `callfred` funciona no hosts, mas o navegador o trata como busca, e ele complica a cobertura da CA na Fase 2. Liberar depois não quebra nada.
3. **Destino em qualquer host.** *Padrão: sim.* O destino pode ser outra máquina, como uma VM ou o IP de um container. O proxy continua escutando só em loopback.
