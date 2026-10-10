# Planejamento: Severino

A ideia é cadastrar `meuapp.sev → 127.0.0.1:3000` numa janela e deixar a ferramenta fazer o resto: gravar o nome no hosts, rotear o tráfego pelo proxy e, se você quiser, emitir um certificado HTTPS confiável. Nenhum passo exige terminal ou edição manual de arquivo.

O domínio é livre. Vale qualquer nome válido, inclusive um que já exista na internet: enquanto a rota estiver ativa, ele passa a apontar para a sua máquina, e o app avisa antes (veja "Domínios" na seção 4). Nos exemplos usamos `.sev`.

Ficam de fora, de propósito: expor serviços na rede ou na internet, Let's Encrypt, balanceamento de carga e resolução de nomes dentro de containers. O proxy escuta só em loopback.

A primeira versão do plano deixava o WSL inteiro de fora. Em 2026-10-09 isso mudou: a Fase 4 traz os serviços do Kubernetes e os containers do Docker para a máquina pelos nomes que eles têm no cluster ou no Compose, descobertos no Windows ou dentro das distros do WSL, e atende apps rodando no WSL (veja o [spec da Fase 4](specs/fase-4-servicos.md)).

Em 2026-10-10 o roadmap ganhou uma Fase 5 de site e publicação (site, deploy pelo GitHub Actions e release com o instalador). Os extras passaram para a Fase 6, que ganhou também a aba DNS e deixa o Helper gravar faixas privadas no bloco DNS (IP público só com UAC).

## 1. Arquitetura

```
 Navegador ──► https://meuapp.sev
                   │  hosts: 127.0.0.1 / ::1
                   ▼
  ┌──────────────────────────────────┐
  │ Severino.exe  (usuário comum)    │
  │  UI WPF  ◄──►  Proxy YARP/Kestrel│──► 127.0.0.1:3000 (seu app)
  │  bandeja       :80  :443         │
  └────────────────┬─────────────────┘
                   │ named pipe ("sincronizar hosts", e só isso)
  ┌────────────────▼─────────────────┐
  │ Severino.Helper (serviço Windows)│──► C:\Windows\System32\drivers\etc\hosts
  └──────────────────────────────────┘
```

São dois processos porque só uma parte precisa de administrador. Editar o hosts exige admin. Abrir as portas 80 e 443 não exige, porque o Windows não tem portas privilegiadas para sockets comuns, e o Kestrel usa sockets comuns.

Por isso o único trecho que precisa de admin fica num serviço minúsculo, instalado uma vez pelo instalador. Isso traz três vantagens:

- **Um único UAC**, na instalação, e nunca mais.
- **O navegador abre normal.** O app principal roda como usuário comum, então "Abrir no navegador" funciona como esperado. Um app elevado abriria o navegador também elevado.
- **Pouco risco.** O serviço só sabe apontar nomes para loopback. Mesmo que outro programa converse com ele, o pior que consegue é desviar um site para a sua própria máquina.

A alternativa seria rodar tudo elevado via Agendador de Tarefas. É mais simples de codar, mas pior nesses três pontos.

## 2. Stack e bibliotecas

| Camada | Escolha | Motivo |
|---|---|---|
| Runtime | .NET 10 (LTS) | Mesmo runtime do pwsh 7, suporte longo |
| Interface | WPF + WPF-UI (lepoco) | Visual Fluent do Windows 11, tema claro/escuro automático, controles prontos |
| MVVM | CommunityToolkit.Mvvm | `[ObservableProperty]` e `[RelayCommand]` via source generator |
| Bandeja | H.NotifyIcon.Wpf | Ícone, menu e notificações na área de notificação |
| Proxy | YARP (`Yarp.ReverseProxy`) | Proxy da Microsoft dentro do processo; rotas em memória com atualização a quente; WebSocket e HTTP/2 nativos, então o HMR funciona |
| Hospedagem | `FrameworkReference Microsoft.AspNetCore.App` no projeto WPF | O Kestrel roda em segundo plano no mesmo processo da UI |
| Certificados | `System.Security.Cryptography` + `System.Formats.Asn1` | CA e certificados gerados sem mkcert ou OpenSSL |
| Chave da CA | `System.Security.Cryptography.ProtectedData` (DPAPI) | Chave privada cifrada e atrelada ao seu usuário |
| Serviço auxiliar | Worker Service + `Microsoft.Extensions.Hosting.WindowsServices` | Serviço Windows enxuto |
| Comunicação | Named pipe (`System.IO.Pipes`) + JSON | Sem porta de rede; ACL restrita ao seu usuário |
| Portas em uso | P/Invoke em `GetExtendedTcpTable` (iphlpapi) | Lista portas escutando com PID e nome do processo |
| Logs | Serilog (arquivo rotativo) + coletor em memória | O arquivo serve para depurar a ferramenta; a memória alimenta a aba de requisições |
| Configuração | System.Text.Json com source generator | JSON legível, UTF-8 sem BOM |
| Testes | xUnit | Testes unitários e de integração |
| Instalador | Inno Setup | Um UAC só: copia os arquivos, registra o serviço, cria atalho |

Considerei três alternativas e descartei:

- **Go + Wails:** o binário fica menor, mas a UI roda em WebView2, que consome mais memória do que parece. E você teria de montar à mão o que o YARP já entrega.
- **Tauri + Rust:** é a opção mais leve, mas TLS dinâmico por SNI e proxy com WebSocket dão bem mais trabalho.
- **PowerShell + WinForms + Caddy:** fica no seu ecossistema, mas a UI é limitada e você passa a gerenciar dois processos e a API do Caddy.

No .NET fica tudo num processo, com acesso direto às APIs do Windows de certificados, serviços e registro.

**Peso:** a publicação é self-contained: o instalador leva o runtime do .NET 10 e fica por volta de 50 MB, mas instala offline e sem pré-requisitos. A primeira versão do plano previa publicar dependente de framework, com poucos MB, e fazer o instalador garantir os runtimes; a troca foi decidida na Fase 3, porque um segundo download atrapalha o "instala sem ajuda". A memória em repouso deve ficar em algumas dezenas de MB.

## 3. Experiência de uso

### Primeira execução

Um assistente de duas telas:

1. **Checagem automática.** Mostra ✓ ou ✗ para cada item: portas 80 e 443 livres, serviço auxiliar respondendo e proxy do sistema fora do caminho. Cada ✗ vem com um botão de correção ou uma explicação.
2. **Primeira rota, com HTTPS opcional.** O formulário de rota embutido traz a caixa "Usar HTTPS". Marcada, ao criar a rota o app gera a CA e abre o diálogo de confirmação do próprio Windows. O HTTPS vem depois da rota porque a CA só pode existir com pelo menos um domínio para restringir (decisão da Fase 2).

O assistente termina abrindo a rota recém-criada no navegador.

### Tela principal

```
┌─ Severino ─────────────────────────────────────── _ □ x ┐
│  Rotas │ Requisições │ Configurações                    │
│                                                         │
│  [+ Nova rota]          Buscar...                       │
│                                                         │
│  ●  meuapp.sev       →  127.0.0.1:3000  https  [on] ⋯ │
│  ●  api.meuapp.sev   →  127.0.0.1:5000  https  [on] ⋯ │
│  ◐  admin.sev          →  127.0.0.1:4200         [on] ⋯ │
│  ○  legado.sev         →  127.0.0.1:8080        [off] ⋯ │
│                                                         │
│  Proxy ativo :80 :443 · hosts ok · HTTPS ok             │
└─────────────────────────────────────────────────────────┘
  ● destino respondendo   ◐ destino fora do ar   ○ desativada
```

Como cada elemento funciona:

- **Domínio:** clicar abre no navegador.
- **Interruptor:** liga e desliga a rota na hora.
- **Menu ⋯:** editar, duplicar, copiar URL e remover.
- **Remover:** não pede confirmação. Aparece "Desfazer" por alguns segundos, o que é mais rápido e menos irritante.
- **Barra inferior:** mostra o estado geral. Clicar num item com problema leva direto à correção.

### Nova rota

```
┌─ Nova rota ────────────────────────────────┐
│ Domínio   [ meuapp.sev               ]   │
│ Destino   [ 127.0.0.1 ] : [ 3000      ▾ ]  │
│               3000 · node (next dev)       │
│               5173 · node (vite)           │
│               5000 · dotnet                │
│ [x] HTTPS    [x] Redirecionar HTTP→HTTPS   │
│ ▸ Avançado                                 │
│                       [Cancelar] [Criar]   │
└────────────────────────────────────────────┘
```

O formulário ajuda em cada campo:

- **Domínio:** nome completo e livre, como `meuapp.sev` ou `api.empresa.com`. O texto de exemplo do campo sugere `.sev`, e uma dica lembra que `.test` e `.localhost` são reservados e nunca colidem com a internet.
- **Porta:** o combo lista as portas que estão escutando, com o nome do processo, então você não precisa lembrar números.
- **Validação inline:** nome inválido ou duplicado aparece em vermelho e bloqueia. Porta sem nada escutando aparece em amarelo, mas não bloqueia: a rota fica aguardando seu servidor subir.
- **Avisos de domínio:** aparecem em amarelo e nunca bloqueiam. Os casos estão em "Domínios", na seção 4: nome que já existe na internet, HSTS preload, `.local`, e domínio fora da cobertura da CA.
- **"Avançado":**
  - preservar o Host original;
  - ignorar certificado inválido do destino, útil para o `https://localhost:5001` do ASP.NET;
  - um campo de observações.

Ao clicar em Criar, a rota fica ativa em cerca de um segundo e aparece "meuapp.sev pronto · Abrir".

### Requisições

Log ao vivo com hora, domínio, método, caminho, status e duração. Dá para filtrar por domínio e pausar. Guarda as últimas 1000 linhas em memória.

### Configurações

Reúne tudo o que é ajustável:

- **Rede:** portas do proxy.
- **Inicialização:** iniciar com o Windows (chave `HKCU\...\Run`, sem admin) e iniciar minimizado.
- **Aparência:** tema.
- **HTTPS:** status da CA, exportar em PEM para Node e Python (com a linha do `NODE_EXTRA_CA_CERTS` pronta para copiar) e remover a CA.
- **Manutenção:** exportar e importar rotas, e o botão "Limpar tudo", que remove o bloco do hosts, a CA e a inicialização automática.

### Bandeja

O ícone muda de cor conforme o estado. O menu traz:

- a lista de rotas (clicar abre no navegador);
- pausar o proxy;
- abrir a janela;
- sair.

Fechar a janela só minimiza para a bandeja. Na primeira vez, um aviso explica isso.

## 4. Detalhes técnicos

### Domínios

Qualquer hostname válido é aceito. O app checa o nome enquanto você digita (com atraso de meio segundo) e de novo ao salvar, e mostra avisos sem bloquear:

- **O nome já existe na internet.** O app consulta o DNS ignorando o hosts e o cache, com `DnsQuery_W` e as flags `DNS_QUERY_NO_HOSTS_FILE | DNS_QUERY_BYPASS_CACHE`, para A, AAAA e CNAME. Uma consulta comum não serviria, porque depois que o Severino grava o nome ela devolve 127.0.0.1. Se o nome resolve, o aviso diz: "`api.empresa.com` existe na internet (resolve para 203.0.113.10). Enquanto esta rota estiver ativa, esta máquina não acessa o site real." Sem rede ou com timeout, o app diz que não conseguiu verificar, sem alarde.
- **HSTS preload.** Alguns TLDs inteiros, como `.dev`, `.app` e `.page`, estão na lista de HSTS preload dos navegadores, então só abrem com HTTPS. O app traz essa lista de TLDs embutida. Nesses casos, liga o HTTPS da rota e explica o motivo. Para nomes que já existem na internet com HTTPS desligado, o aviso acrescenta que o site real pode usar HSTS e exigir HTTPS.
- **`.local`.** Conflita com mDNS e deixa a resolução lenta.
- **Fora da cobertura da CA.** Com HTTPS ativo, cadastrar um domínio que a CA atual não cobre exige reemitir a CA (veja "HTTPS").

### Hosts

O serviço auxiliar mantém um bloco delimitado e nunca toca no resto do arquivo:

```
# >>> Severino managed block (do not edit)
127.0.0.1  meuapp.sev
::1        meuapp.sev
# <<< Severino
```

A cada sincronização, o serviço faz o seguinte:

1. Lê o arquivo e substitui só o bloco, preservando quebras CRLF.
2. Grava num arquivo temporário e troca com `File.Replace`, gerando `hosts.severino.bak`.
3. Remove e restaura o atributo somente-leitura, se ele existir.
4. O bloco é ASCII puro, sem BOM, por isso os marcadores ficam em inglês e sem acento. O resto do arquivo é preservado byte a byte, qualquer que seja a codificação.
5. Chama `DnsFlushResolverCache`, que equivale ao `ipconfig /flushdns`.

Domínios com acento viram punycode via `IdnMapping`. O serviço valida tudo por conta própria, sem confiar no app: sintaxe de hostname (rótulos e tamanho), nada de quebra de linha ou espaço, limite de entradas e endereços: loopback no bloco das rotas e serviços; no bloco DNS da Fase 6, loopback e faixas privadas, e IP público só com aprovação por UAC. Não há restrição de sufixo.

O hosts não aceita curinga, então no MVP cada subdomínio precisa de rota própria.

### Proxy

O Kestrel escuta em `127.0.0.1` e `[::1]`. Cada rota vira uma `RouteConfig` do YARP com `Match.Hosts = [domínio]` e um cluster apontando para o destino. Alterações chamam `InMemoryConfigProvider.Update`, sem reiniciar nada.

Ajustes pensados para dev:

- **Timeout longo.** O `ActivityTimeout` fica em 10 minutos. Um breakpoint no backend segura a requisição, e o padrão de 100 segundos devolveria 504 no meio da depuração.
- **Sem limite de corpo.** O limite de tamanho da requisição fica desligado, para permitir uploads grandes.
- **Host reescrito por padrão.** O Host vai como o do destino, acompanhado de `X-Forwarded-Host/Proto/For`. Isso agrada o Vite e o webpack-dev-server, que bloqueiam Host desconhecido. A opção "Preservar Host original" cobre apps que geram URLs absolutas.
- **Redirecionamento temporário.** HTTP→HTTPS usa 307, nunca 301, e o proxy nunca envia HSTS. Os dois ficam gravados no navegador e viram dor de cabeça quando você desliga o HTTPS.
- **Páginas de erro próprias.** Domínio desconhecido mostra um 404 com a lista de rotas. Destino fora do ar mostra um 502 dizendo `meuapp.sev → 127.0.0.1:3000 não respondeu. Seu servidor está rodando?`
- **Proteção contra loop.** O destino não pode ser o próprio proxy.

### HTTPS

Na ativação, o app gera uma CA raiz ECDSA P-256 com validade de 10 anos.

- **Name Constraints:** a CA só vale para os domínios cadastrados. Mesmo que a chave vaze, ela não serve para falsificar `banco.com.br`, a menos que você mesmo tenha cadastrado esse domínio. A cobertura é calculada assim:
  - **TLD que não existe na internet** (`.sev`, ou os reservados `.test`, `.localhost` e `.internal`): a CA cobre o TLD inteiro. Assim, novas rotas `*.sev` nunca pedem reemissão. Para saber se o TLD existe, o app consulta o SOA dele na raiz do DNS.
  - **TLD real** (`api.empresa.com`): a CA cobre exatamente o nome cadastrado, com seus subdomínios.
- **Reemissão:** cadastrar um domínio fora da cobertura exige uma CA nova. A UI explica e, num clique, gera a CA, instala (o Windows pede confirmação de novo), remove a antiga e reemite os certificados das rotas. Remover rotas não reemite nada, para não pedir confirmação à toa. Configurações › HTTPS mostra os domínios cobertos e permite reemitir para enxugar a lista.
- **Chave privada:** cifrada com DPAPI.
- **Instalação:** a CA vai para `CurrentUser\Root`. Isso dispara o aviso de segurança do Windows, que funciona como consentimento explícito, sem exigir admin.

Os certificados de cada domínio são emitidos sob demanda no primeiro acesso, pelo `ServerCertificateSelector` do Kestrel (via SNI). Eles valem 397 dias, ficam em cache na memória e no disco e são renovados automaticamente quando faltam 30 dias.

Há uma armadilha conhecida do Windows: certificado com chave efêmera criado em memória falha no SslStream. Antes de entregar ao Kestrel, é preciso exportar para PFX e recarregar com `X509CertificateLoader.LoadPkcs12`.

Edge e Chrome usam o repositório do Windows. O Firefox pode precisar de `security.enterprise_roots.enabled = true`; o app detecta isso e mostra o passo.

### Diagnósticos

**Porta ocupada.** Se a 80 ou a 443 estiver em uso, a UI mostra qual processo é o dono. PID 4 ("System") significa http.sys, quase sempre IIS ou outro serviço registrado. Outros suspeitos comuns são XAMPP, Docker publicando a 80 e VMware na 443. Como alternativa, o app oferece usar 8080 e 8443, e as URLs passam a levar a porta.

**Proxy do sistema.** Com proxy configurado no Windows, comum em VPN corporativa, o navegador mandaria os domínios das rotas para fora. O app detecta isso nas Internet Settings do HKCU e oferece adicionar esses domínios às exceções.

**Saúde dos destinos.** Um teste de conexão TCP a cada 5 segundos por rota ativa alimenta a bolinha de status.

### Desinstalação limpa

O desinstalador faz três coisas, nesta ordem:

1. Pede ao serviço para remover o bloco do hosts.
2. Roda `Severino.exe --cleanup`, para tirar a CA do repositório do usuário. O Inno Setup não aceita `runasoriginaluser` na desinstalação, então ele roda com a conta que aprovou o UAC, que no caso comum é o próprio usuário. Quando a instalação foi de outra conta, o desinstalador avisa antes e sugere "Limpar tudo" com aquele usuário (confirmado na [Fase 3](specs/fase-3-polimento.md)).
3. Remove o serviço.

## 5. Modelo de dados

Fica em `%LOCALAPPDATA%\Severino\config.json`, com as últimas 5 versões guardadas como backup em `backups\`. A gravação é atômica (arquivo temporário + troca). Um arquivo ilegível é movido para `config.invalid-<data>.json` e o app sobe com a configuração padrão; um arquivo de versão mais nova que a suportada não é tocado, e o app avisa e fecha. O bloco `state` guarda marcas internas do app, como o aviso de "fechar minimiza para a bandeja" já exibido; não aparece em Configurações.

```json
{
  "version": 1,
  "settings": {
    "httpPort": 80,
    "httpsPort": 443,
    "startWithWindows": true,
    "startMinimized": true,
    "theme": "auto"
  },
  "state": {
    "closeToTrayHintShown": true
  },
  "routes": [
    {
      "id": "3f2c9a1e-7b44-4d0e-9c1a-2e5b8f6d0a11",
      "domain": "meuapp.sev",
      "target": "http://127.0.0.1:3000",
      "enabled": true,
      "https": true,
      "redirectToHttps": true,
      "preserveHost": false,
      "ignoreTargetCertErrors": false,
      "notes": ""
    }
  ]
}
```

## 6. Estrutura da solução

```
Severino/
├─ src/
│  ├─ Severino.App/        WPF: janelas, bandeja, ViewModels, hospeda o proxy
│  ├─ Severino.Core/       modelos, configuração, validação, cliente do pipe
│  ├─ Severino.Proxy/      YARP, Kestrel, CA e emissão de certificados, páginas de erro
│  ├─ Severino.Contracts/  mensagens do pipe e validador de domínio (compartilhado)
│  └─ Severino.Helper/     serviço Windows: hosts + flush de DNS
├─ tests/
│  └─ Severino.Tests/
└─ installer/
   └─ severino.iss
```

O `Contracts` existe para que app e serviço usem exatamente o mesmo validador. O serviço não depende de mais nada.

## 7. Roadmap

| Fase | Entrega | Pronto quando |
|---|---|---|
| 0. Esqueleto | Solução, janela WPF-UI com abas, bandeja, instância única, carregar e salvar config | O app abre, minimiza para a bandeja e reabre sem duplicar |
| 1. MVP HTTP | CRUD de rotas, YARP, serviço auxiliar + bloco no hosts, avisos de domínio, status de saúde, páginas de erro, detecção de porta ocupada | `http://meuapp.sev` abre seu app e o HMR do Vite funciona |
| 2. HTTPS | CA com Name Constraints nos domínios cadastrados e reemissão, emissão por SNI, redirecionamento, exportar CA | `https://meuapp.sev` abre no Edge e no Chrome sem aviso |
| 3. Polimento | Assistente de primeira execução, aba de requisições, combo de portas com processo, detector de proxy do sistema, iniciar com Windows, importar e exportar, "Limpar tudo", instalador | Alguém que nunca viu a ferramenta instala e cria uma rota sem ajuda |
| 4. Serviços | Rotas de serviço com encaminhamento TCP e loopback dedicado; descoberta de services do Kubernetes e containers do Docker, no Windows e no WSL, ou colando a saída dos comandos; nomes no `/etc/hosts` das distros | Um app no Windows ou no WSL chama `pedidos:8080` e chega ao serviço no cluster, pelo gateway, com o nome original |
| 5. Site e publicação | Site do Severino; pipeline do GitHub Actions que publica o site; pipeline de build que gera a versão e cria a release no GitHub com o instalador `.exe` anexado ([spec](specs/fase-5-site.md)) | O site está no ar, e um clique no Actions publica a próxima versão com o `.exe` |
| 6. DNS e extras | Aba DNS para entradas `nome → IP` persistentes, importando o hosts feito à mão, e integrada a rotas e serviços (nome como destino); curinga via DNS embutido + regra NRPT (a validar), rotas por caminho (`/api`), grupos de rotas, `kubectl port-forward` gerenciado, acompanhar mudanças do cluster e do Docker sozinho, módulo PowerShell ([spec](specs/fase-6-dns-e-extras.md)) | Os critérios do spec |

## 8. Riscos restantes

| Risco | Mitigação |
|---|---|
| Defender acusar o hosts de sequestro (HostsFileHijack) | O alerta mira redirecionamento de domínios conhecidos. Nomes inexistentes, como `*.sev`, não devem disparar. Para nomes que existem na internet, o aviso do formulário menciona esse risco |
| TLD `.sev` passar a existir (nova rodada de TLDs da ICANN) | O hosts continua tendo prioridade. O aviso de "domínio existe" passa a aparecer, e a cobertura da CA cai de TLD inteiro para nome a nome na próxima reemissão |
| Barra de endereço tratar TLD desconhecido como busca | "Abrir no navegador" sempre usa a URL completa. Na primeira vez, digitar `http://` ou a barra final; depois o histórico resolve |
| DNS-over-HTTPS no navegador | Os navegadores consultam o hosts antes do DoH; se algo falhar, o diagnóstico sugere testar com `.localhost` |
| SmartScreen em executável sem assinatura | Aceitável para uso próprio; certificado de assinatura só se for distribuir |
| Containers não enxergam `.sev` | Fora do escopo, documentado. Apps no WSL passam a enxergar as rotas de serviço na Fase 4 |
| Porta 53 ocupada no DNS da Fase 6 | Validar antes de implementar o curinga |

## 9. Testes

Os testes unitários cobrem os pontos onde um erro custa caro:

- **Mesclagem do hosts:** idempotência, preservação das linhas fora do bloco, CRLF/LF misturados, arquivo somente-leitura, bloco corrompido.
- **Validador de domínio:** rótulos e tamanho, punycode, tentativas de injeção de linha.
- **Cobertura da CA:** TLD inexistente vira TLD inteiro, TLD real vira nome exato, sem duplicar nomes já cobertos.
- **Migração de versões da configuração.**

Os testes de integração sobem Kestrel e YARP em portas aleatórias com um backend falso. Eles verificam o roteamento por Host, a passagem de WebSocket, a página 502 e o redirecionamento 307. O teste de certificados confere se a folha encadeia na CA, se o SAN está correto e se a Name Constraint rejeita domínios não cobertos.

Para fechar cada fase, um checklist manual curto: porta 80 ocupada, serviço parado, HMR do Vite, Firefox e desinstalação limpa.
