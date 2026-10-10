# Spec: Fase 3, Polimento

**Status:** aprovado em 2026-10-09, com os padrões das "Decisões a confirmar". As divergências 1 e 2 foram registradas no plano.
**Base:** [planejamento.md](../planejamento.md), seções 3 ("Primeira execução", "Requisições", "Configurações", "Bandeja"), 4 ("Diagnósticos", "Desinstalação limpa") e 7. Inclui o que o [spec da Fase 2](fase-2-https.md) adiou para cá. Segue o formato dos specs anteriores; divergências do plano ficam em "Decisões a confirmar".
**Pré-requisito:** a Fase 2 fechada pelo [guia de testes](../testes/fase-2-https.md).

## Objetivo

Alguém que nunca viu o Severino baixa um instalador, instala com um único UAC e cria a primeira rota sem abrir terminal nem ler documentação. No dia a dia, o app mostra o que passa pelo proxy e resolve sozinho os tropeços comuns: porta ocupada, proxy corporativo e Firefox.

## Critérios de pronto

Cada item é verificável e binário. A fase só fecha com todos marcados.

1. **Instalação.** Numa máquina limpa (Windows Sandbox), `Severino-Setup-<versão>.exe` instala com um único UAC: copia os arquivos para `Program Files\Severino`, registra e inicia o `Severino.Helper`, cria o atalho no Menu Iniciar e abre o app.
2. **Primeira execução.** Na mesma máquina, o assistente aparece uma única vez, mostra ✓ ou ✗ para cada checagem, com correção ou explicação, e termina com uma rota criada e aberta no navegador. Tudo pela interface, sem terminal.
3. **Desinstalação limpa.** Depois de desinstalar:
   - o hosts não tem o bloco do Severino;
   - a CA saiu de `CurrentUser\Root`;
   - o serviço e a pasta em `Program Files` sumiram;
   - a entrada de inicialização automática sumiu;
   - as exceções de proxy que o app adicionou foram removidas, e só elas.
4. **Requisições.** A aba mostra ao vivo hora, domínio, método, caminho, status e duração, inclusive as respostas do próprio proxy (404, 307, 502). Filtra por domínio, pausa, limpa e guarda as últimas 1000 linhas. Uma rajada de 100 requisições por segundo não trava a interface.
5. **Portas no formulário.** O campo de porta do destino lista as portas escutando na máquina, com o nome do processo, como `5173 · node`.
6. **Proxy do sistema.** Com um proxy ligado no Windows, o app avisa na checagem e na barra inferior. "Adicionar exceções" faz o Edge e o Chrome abrirem as rotas sem passar pelo proxy.
7. **Iniciar com o Windows.** Ligar a opção cria a entrada em `HKCU\...\Run`, e desligar remove. Depois de sair e entrar no Windows, o app está na bandeja.
8. **Importar e exportar.** Exportar grava as rotas num JSON, e importar esse arquivo em outra máquina recria as rotas. Domínios em conflito são listados, não sobrescritos em silêncio. Rotas HTTPS fora da CA oferecem reemitir.
9. **Limpar tudo.** Desfaz o que o app mudou no sistema, a mesma lista do critério 3 exceto o serviço e os arquivos, e fecha o app.
10. **Bandeja.**
    - O ícone muda conforme o estado: normal, com problema ou pausado.
    - O menu lista as rotas ligadas, e clicar numa abre no navegador.
    - "Pausar" libera as portas e tira o bloco do hosts; "Retomar" desfaz.
11. **Firefox.** Se o Firefox estiver instalado sem confiar nas CAs do Windows, o cartão HTTPS mostra o passo para corrigir. Nos outros casos, não mostra nada.
12. **Testes.** `dotnet test` passa com os testes listados em "Testes", e o build segue sem avisos.

## Validações iniciais

São perguntas que mudam o desenho. Vêm antes de tudo, como testes descartáveis.

- **Limpeza da CA na desinstalação.** O plano diz que o desinstalador roda `Severino.exe --cleanup` "como o usuário original", com a flag `runasoriginaluser` do Inno Setup. Pela documentação do Inno, essa flag só existe em `[Run]`, na instalação, e não em `[UninstallRun]`. Um desinstalador aberto pelas Configurações do Windows já nasce elevado, sem um "usuário original" para onde voltar. É preciso confirmar o que acontece:
  - com a mesma conta elevada, o caso comum, o `CurrentUser\Root` é o do usuário e o `--cleanup` funciona direto;
  - com outra conta de administrador, a CA não sai.
  
  Se for isso, a CA fica órfã nesse caso raro, mas inofensiva, porque a chave é apagada junto com a pasta do usuário. O desinstalador avisa.
- **SID do usuário no instalador.** O Helper só aceita o SID gravado em `HKLM\SOFTWARE\Severino\Helper\AllowedUserSid`. O instalador precisa do SID de quem o abriu, não do administrador que aprovou o UAC. Hipótese: `ExecAsOriginalUser` roda `whoami /user /fo csv` num arquivo temporário, e o `[Code]` lê o resultado. Confirmar também com outra conta de administrador.
- **Exceções de proxy.** Confirmar que o Edge e o Chrome respeitam `*.sev` em `ProxyOverride` logo depois da gravação, com `InternetSetOption(INTERNET_OPTION_SETTINGS_CHANGED)`, sem reiniciar o navegador. Confirmar também o que acontece com um proxy por script (PAC, em `AutoConfigURL`), que o app não consegue alterar.
- **Firefox.** Desde a versão 120, o Firefox importaria as CAs do Windows por padrão. Se isso se confirmar, a detecção se resume a procurar `security.enterprise_roots.enabled = false` explícito no `prefs.js` de algum perfil. O Firefox não está instalado nesta máquina; a validação usa um Windows Sandbox.
- **Custo do log de requisições.** Medir o middleware sob carga (`bombardier` ou um loop de `HttpClient`) para confirmar que o custo é desprezível perto do YARP.

**Resultados:**

- **Custo do log (2026-10-09):** 40 mil requisições com 32 conexões, alternando proxy sem e com log. Deu 1295 e 1774 req/s sem log, contra 1414 e 1477 com log. A variação entre rodadas iguais é maior que a diferença entre as duas versões, então o custo fica abaixo do ruído.
- **As outras quatro** dependem do Inno Setup, de um Windows Sandbox ou de alterar o registro do usuário. Elas são feitas no começo do passo que depende de cada uma: proxy e Firefox no passo 6, instalador no passo 8.

## Escopo

### Requisições (`Proxy` + `App`)

- **Coleta:** um middleware no começo do pipeline dos dois listeners grava hora, esquema, domínio, método, caminho com query, status, duração e se a resposta veio do próprio proxy: 404, 307, 502 e 504.
- **Momento do registro:**
  - a linha entra quando a resposta termina;
  - WebSocket entra no upgrade, com status 101, e a duração mostra "aberto" até a conexão fechar.
- **Memória:** um buffer circular de 1000 entradas, dentro do `Severino.Proxy`. Nada vai para o disco, porque caminhos e queries podem carregar tokens.
- **Interface:**
  - lista virtualizada, com as entradas mais novas no topo;
  - o status colorido por faixa (2xx, 3xx, 4xx, 5xx), e as respostas do proxy marcadas como "Severino";
  - filtro por domínio, com "Todos" e as rotas cadastradas;
  - "Pausar" congela a lista sem parar a coleta e mostra "N novas" até retomar;
  - "Limpar";
  - menu de contexto com "Copiar URL" e "Abrir no navegador".
- **Desempenho:** a interface recebe as entradas em lotes, no máximo a cada 200 ms.
- **Estado vazio:** o texto atual some ("As requisições que passarem pelo proxy aparecem aqui"). Com o proxy parado, a aba diz isso.

### Bandeja

- **Estados do ícone**, gerados pelo `scripts/build-icons.cs` a partir do mesmo rosto:
  - normal;
  - problema, com um ponto laranja, quando a barra inferior tem algum item com problema;
  - pausado, dessaturado.
- **Menu:**
  - "Abrir Severino";
  - as rotas ligadas, em que clicar abre no navegador com `https` quando houver;
  - "Pausar" ou "Retomar";
  - "Sair".
- **Pausar:** para os dois listeners e tira o bloco do hosts, então os domínios voltam a resolver como na internet. "Retomar" restaura os dois. O estado não persiste: o app sempre abre ativo.

### Configurações

- **Iniciar com o Windows:** grava em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` o caminho do executável atual com `--autostart`. Com `--autostart`, o app abre na bandeja.
- **Exportar rotas:** grava `severino-rotas.json` com `{ "severino": 1, "routes": [...] }`, sem os ids.
- **Importar rotas:**
  - valida cada rota com as mesmas regras do formulário;
  - acrescenta as novas e pula os domínios que já existem, listando os pulados e os inválidos num resumo;
  - se alguma rota importada tiver HTTPS e estiver fora da CA, oferece reemitir, como no formulário.
- **Limpar tudo:**
  - confirma antes;
  - tira o bloco do hosts, remove a CA (o Windows pede confirmação), a entrada de inicialização automática e as exceções de proxy adicionadas pelo app;
  - fecha o app.
  
  As rotas ficam no `config.json`, a menos que a pessoa marque "Apagar também as rotas e configurações".
- **Linha de comando:** `Severino.exe --cleanup` faz o mesmo que "Limpar tudo", sem interface e sem confirmação. Ele não mexe no hosts, que é tarefa do Helper (`--clear-hosts`). Termina com código 0 quando tudo saiu.

### Formulário de rota

- **Porta do destino:** vira um combo editável com as portas escutando em loopback ou em qualquer endereço, como `3000 · node` ou `5000 · dotnet`. A lista se atualiza ao abrir o combo.
  - Ficam de fora as portas do próprio Severino e as do Windows abaixo de 1024, exceto 80 e 443 de outros processos.
  - Quando a linha de comando do processo for legível, a lista acrescenta uma dica curta, como `5173 · node (vite)`, reconhecendo vite, next, nuxt, astro, webpack e `dotnet watch`. Sem acesso, fica só o nome.

### Proxy do sistema

- **Detecção:** lê `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings`.
  - O proxy está ativo quando `ProxyEnable = 1` e `ProxyServer` não está vazio. `ProxyServer` preenchido com `ProxyEnable = 0` é comum e não conta.
  - O proxy por script (`AutoConfigURL`) só gera aviso, porque o app não sabe o que o script decide.
- **Rotas afetadas:** as rotas ligadas que o `ProxyOverride` atual não cobre. `<local>` só cobre nomes sem ponto, então não serve para `callfred.sev`.
- **Correção:** "Adicionar exceções" acrescenta ao `ProxyOverride` `*.<tld>` para TLDs que não existem na internet, a mesma regra da cobertura da CA, e o nome exato nos outros casos. Depois avisa o WinINet.
  - O que foi adicionado fica em `state.proxyBypassAdded`, para "Limpar tudo" e `--cleanup` removerem só isso.
  - Nunca é automático: a pessoa clica.
- **Onde aparece:** como checagem no assistente e como item na barra inferior ("proxy do sistema no caminho"), que leva à correção.

### Firefox

O cartão HTTPS ganha uma checagem: se o Firefox estiver instalado e algum perfil tiver `security.enterprise_roots.enabled` desligado, o cartão mostra o passo, com o caminho em `about:config`. Sem Firefox, ou com a importação ligada, não mostra nada. O texto fixo atual sobre o Firefox some.

### Assistente de primeira execução

Aparece quando `state.firstRunCompleted` é falso, numa janela própria sobre a principal.

1. **Checagem**, com ✓ ou ✗ por item e a correção ao lado:
   - serviço auxiliar respondendo, com a explicação de reinstalar se não;
   - porta 80 livre ou já do Severino, com o dono e "Usar 8080" se estiver ocupada;
   - proxy do sistema fora do caminho, com "Adicionar exceções" se não estiver.
   
   Os itens se reavaliam sozinhos depois de cada correção.
2. **Primeira rota:** o formulário de rota embutido, com o domínio sugerido `meuapp.sev` e o combo de portas, mais uma caixa "Usar HTTPS (o Windows pede confirmação)". Ao criar, ativa o HTTPS se marcado, com o diálogo de explicação da Fase 2. Depois abre a rota no navegador.

"Pular" em qualquer passo encerra o assistente, que não volta mais. Ele fica disponível em Configurações › Manutenção › "Rever checagem".

### Instalador (`installer/severino.iss`)

- **Ferramenta:** Inno Setup 6, compilado por `scripts/build-installer.ps1`, que roda `dotnet publish` no App e no Helper e depois o `ISCC`.
- **Destino:** `Program Files\Severino`, por máquina. É obrigatório: o Helper roda como SYSTEM, e uma pasta onde o usuário escreve deixaria qualquer programa dele trocar o binário.
- **Instalação:**
  1. grava `AllowedUserSid` com o SID de quem abriu o instalador (ver "Validações iniciais");
  2. registra o serviço `Severino.Helper`, automático, e o inicia;
  3. cria o atalho no Menu Iniciar e, opcionalmente, na área de trabalho;
  4. no fim, oferece "Abrir o Severino", como o usuário original (`runasoriginaluser`).
- **Atualização:** instalar por cima para o app e o serviço, troca os arquivos e reinicia o serviço, preservando `%LOCALAPPDATA%\Severino`.
- **Desinstalação**, nesta ordem:
  1. fecha o app, se aberto;
  2. `Severino.Helper.exe --clear-hosts`;
  3. `Severino.exe --cleanup`, com a ressalva das "Validações iniciais";
  4. remove o serviço, a pasta e a chave do registro;
  5. pergunta se apaga `%LOCALAPPDATA%\Severino`, com "Não" como padrão.
- **Assinatura:** nenhuma. O SmartScreen avisa, o que é aceitável para uso próprio, como diz o plano.
- **Desenvolvimento:** o `scripts/dev-helper.ps1` continua para quem roda pelo código-fonte.

## Fora do escopo

- **Fase 4:** curinga por DNS, rotas por caminho, importar entradas do hosts, grupos de rotas e módulo PowerShell.
- **Sem planos:** atualização automática, assinatura de código, instalação para vários usuários na mesma máquina (só quem instala usa o Helper) e ARM64.

## Testes

**Unitários**

- **Log de requisições:** o buffer circular descarta as mais antigas depois de 1000 e é seguro sob concorrência; o filtro por domínio; os lotes para a interface.
- **Proxy do sistema:**
  - leitura de `ProxyEnable`, `ProxyServer`, `ProxyOverride` e `AutoConfigURL`, a partir de valores falsos, sem tocar no registro real;
  - quais rotas o `ProxyOverride` cobre, incluindo `<local>`, curingas e maiúsculas;
  - adicionar e remover só as entradas do app, preservando a ordem e as entradas da empresa.
- **Importar e exportar:** ida e volta; arquivo de outra versão; conflitos e rotas inválidas no resumo.
- **Inicialização automática:** grava e remove a entrada num caminho de registro de teste.
- **Portas:** a lista de portas escutando inclui um `TcpListener` aberto pelo teste, com o PID do próprio processo; e a dica de ferramenta a partir de linhas de comando de exemplo.
- **Firefox:** leitura de `prefs.js` com a preferência ausente, `true` e `false`.

**Integração**

- **Middleware de log:** 200, 404 do proxy, 307 e 502 entram com o status e a marca certos; WebSocket entra no upgrade.
- **Pausar e retomar:** as portas ficam livres e voltam, e o `HostsSync` recebe a lista vazia e depois a cheia.
- **`--cleanup`:** com o `FakeTrustStore` e um diretório temporário, remove a CA, a entrada de inicialização e as exceções.

**Checklist manual** (num Windows Sandbox, no fim da fase)

- os critérios de pronto 1, 2, 3, 6, 7 e 11;
- atualizar por cima de uma versão anterior preserva as rotas;
- instalar com uma conta e aprovar o UAC com outra conta de administrador.

## Ordem de implementação

1. Validações iniciais.
2. Requisições: coleta, buffer e aba. É independente de tudo e preenche a aba que hoje está vazia.
3. Bandeja: estados, rotas e pausar.
4. Configurações: iniciar com o Windows, importar e exportar, "Limpar tudo" e `--cleanup`.
5. Combo de portas no formulário.
6. Proxy do sistema e Firefox.
7. Assistente de primeira execução, que reaproveita as checagens dos passos 5 e 6.
8. Instalador, publicação e desinstalação.
9. Checklist manual e ajustes.

## Pacotes e ferramentas novos

- **Inno Setup 6**, só na máquina de build: `winget install JRSoftware.InnoSetup`.
- **`System.Management`**, para ler a linha de comando dos processos (WMI `Win32_Process`) no combo de portas. A alternativa sem pacote é ler o PEB de cada processo por P/Invoke, que é mais frágil.

## Decisões a confirmar

Cada item já tem um padrão adotado neste spec. Basta confirmar ou trocar.

1. **Publicação self-contained.** *Padrão: sim, o que diverge do plano.* O plano previa publicar dependente de framework e fazer o instalador garantir os runtimes Desktop e ASP.NET Core do .NET 10. Self-contained deixa o instalador maior, por volta de 50 MB contra uns 5 MB, mas instala offline, sem um segundo download, e funciona no Windows Sandbox sem preparo. Isso pesa direto no critério "instala sem ajuda". Se aprovado, o plano é atualizado.
2. **O assistente cria a rota antes do HTTPS.** *Padrão: sim, o que diverge do plano.* O plano põe "Ativar HTTPS" no segundo passo, antes de existir rota, mas a Fase 2 decidiu que a CA exige pelo menos um domínio. O HTTPS vira uma caixa no formulário da primeira rota.
3. **"Pausar" também tira o bloco do hosts.** *Padrão: sim.* Pausado, o Severino sai do caminho por inteiro, e os domínios voltam a resolver como na internet. A alternativa só parar as portas deixaria os domínios apontando para um proxy que não responde.
4. **"Limpar tudo" preserva as rotas.** *Padrão: sim*, com uma caixa para apagar também. Assim dá para limpar o sistema e reabrir o app depois sem recadastrar nada.
5. **Exceções de proxy por TLD quando o TLD não existe.** *Padrão: sim* (`*.sev`). Novas rotas `.sev` não pedem nova exceção. Para TLDs reais, só o nome exato.
6. **Abrir pela inicialização automática sempre vai para a bandeja.** *Padrão: sim.* A opção "Iniciar minimizado" passa a valer só para quem abre o app à mão.
7. **Importar pula domínios existentes.** *Padrão: pular e listar.* A alternativa seria perguntar rota a rota se substitui.
8. **A desinstalação pergunta antes de apagar `%LOCALAPPDATA%\Severino`.** *Padrão: perguntar, com "Não" pré-selecionado.* Reinstalar depois recupera as rotas.
