# Spec: Fase 2, HTTPS

**Status:** aprovado em 2026-10-09, com os padrões das "Decisões a confirmar"
**Base:** [planejamento.md](../planejamento.md), seção 4 ("Domínios" e "HTTPS") e seção 7. Segue o formato do [spec da Fase 1](fase-1-mvp-http.md). Divergências ficam em "Decisões a confirmar".

## Objetivo

Ativar o HTTPS local num clique e abrir `https://callfred.sev` no Edge e no Chrome com o cadeado normal, sem aviso. A CA criada para isso só vale para os domínios cadastrados, então não serve para falsificar outros sites.

## Critérios de pronto

Cada item é verificável e binário. A fase só fecha com todos marcados.

1. **Ativação.** "Ativar HTTPS" em Configurações gera a CA. O Windows pede confirmação uma vez, e a CA aparece em `CurrentUser\Root` com Name Constraints que cobrem os domínios das rotas.
2. **Navegação.** `https://callfred.sev` abre no Edge e no Chrome sem aviso, por HTTP/2, e o HMR do Vite funciona por `wss://`.
3. **Redirecionamento.** Com "Redirecionar HTTP→HTTPS" ligado, `http://callfred.sev/x?y=1` responde 307 para `https://callfred.sev/x?y=1`. Nenhuma resposta do proxy traz `Strict-Transport-Security`.
4. **Cobertura sem nova confirmação.** Com a CA cobrindo `.sev`, criar `api.sev` com HTTPS funciona na hora, sem nova confirmação do Windows.
5. **Reemissão.** Criar `api.empresa.com` (um TLD real) com HTTPS oferece reemitir a CA. Depois das confirmações do Windows, `https://api.empresa.com` abre sem aviso, a CA antiga sai do repositório e os certificados das rotas existentes continuam válidos.
6. **Name Constraints valendo.** Um certificado de folha para `banco.com.br` assinado pela chave da CA é rejeitado: por teste automatizado na cadeia do Windows, se ela aplicar a restrição (ver "Validações iniciais"), e manualmente no Chrome.
7. **Exportar CA.** "Exportar CA" grava um PEM, e `NODE_EXTRA_CA_CERTS=<arquivo> node -e "fetch('https://callfred.sev').then(r => console.log(r.status))"` imprime 200.
8. **Porta 443 ocupada.** Recebe o mesmo tratamento da 80: mostra o dono, permite trocar a porta sem reiniciar o app, e o HTTP segue funcionando enquanto isso.
9. **Chaves e remoção.** No disco, as chaves privadas só existem cifradas com DPAPI. "Remover CA" tira a CA do repositório e apaga chaves e certificados, e o HTTPS para de responder.
10. **Testes.** `dotnet test` passa com os testes listados em "Testes", e o build segue sem avisos.

## Validações iniciais

São perguntas que mudam o desenho. Vêm antes de tudo, como um teste descartável de no máximo meio dia.

- **A cadeia do Windows aplica Name Constraints da raiz?** Se aplicar, o critério 6 vira teste automatizado com `X509Chain`. Se não aplicar, a proteção depende do validador de cada cliente: Chrome e Edge usam o próprio, que aplica, e o Firefox também. Nesse caso o risco fica documentado para clientes que usam o validador do Windows, como `curl` com Schannel, .NET e PowerShell.
- **Remover a CA antiga de `CurrentUser\Root` também pede confirmação?** Isso define se a reemissão custa uma ou duas confirmações, e o texto da interface precisa dizer o número certo.
- **O `SslStream` aceita a chave do PKCS#12 recarregado?** A armadilha do plano é que um certificado com chave efêmera falha no Schannel. O teste confirma que recarregar com `X509CertificateLoader.LoadPkcs12` e `X509KeyStorageFlags` padrão resolve.

**Resultados (2026-10-09, Windows 11, .NET 10.0.12):**

- **Name Constraints na raiz:** a cadeia do Windows aplica. Com `CustomRootTrust`, a folha `banco.com.br` assinada pela CA restrita a `sev` é rejeitada com `HasNotPermittedNameConstraint`, e a mesma folha passa numa CA de controle sem restrição. O critério 6 vira teste automatizado.
- **Chave efêmera:** confirmada a armadilha. A chave criada em memória com `CopyWithPrivateKey` derruba o handshake. Depois de exportar e recarregar com `LoadPkcs12`, o handshake funciona em TLS 1.2 e 1.3, tanto com as flags padrão quanto com `EphemeralKeySet`. Ficam as flags padrão, como no plano, porque o `EphemeralKeySet` não foi validado em builds antigos do Windows 10.
- **Validade:** a validade da folha não pode passar da validade da CA; o `CertificateRequest.Create` recusa. A emissão limita o `notAfter` à validade da CA.
- **Confirmação ao remover:** pede. Verificado no checklist manual: a reemissão custa duas confirmações do Windows, uma para instalar a CA nova e outra para remover a antiga, e "Remover CA" custa uma. A interface diz isso antes do aviso.
- **Dono do aviso (achado no checklist):** o aviso do Windows abre sem janela dona, chamado de qualquer thread, e pode ficar atrás do app. O app chama da thread da interface, prende cada aviso à janela principal enquanto ele está aberto e, antes dele, mostra um diálogo próprio com o nome e a impressão digital que o Windows vai exibir.

## Escopo

### Cobertura da CA (`Core`)

Uma função pura calcula o conjunto de nomes permitidos a partir dos domínios das rotas com HTTPS, ligadas ou não. Assim, religar uma rota nunca exige reemissão.

- **TLD inexistente:** cobre o TLD inteiro (`sev`). Isso vale para os reservados `test`, `localhost`, `internal`, `example` e `invalid`, e para qualquer TLD cuja consulta SOA na raiz do DNS responda NXDOMAIN.
- **TLD real:** cobre o nome exato (`api.empresa.com`), o que inclui os subdomínios dele.
- **Sem resposta do DNS:** sem rede ou com timeout, cobre o nome exato. É a opção conservadora: na pior das hipóteses, depois haverá uma reemissão a mais.
- **Cache:** o resultado da consulta de cada TLD fica em `state.tldExists` no `config.json`, para a cobertura não mudar de uma execução para outra.
- **Redundância:** nomes já cobertos por outro são removidos. Se `sev` está no conjunto, `a.sev` sai.
- **Pergunta auxiliar:** `IsCovered(domínio, nomesDaCA)` decide se um domínio cabe na CA atual.

`IDnsResolver` ganha uma consulta SOA, usando o mesmo `DnsQuery_W`.

### Autoridade certificadora (`Proxy/Certificates`)

- **Raiz:** ECDSA P-256 e SHA-256, validade de 10 anos, `CN=Severino Local CA (<usuário>@<máquina>)`.
- **Extensões da raiz:**
  - BasicConstraints `CA:true, pathlen:0`, crítica;
  - KeyUsage `keyCertSign | cRLSign`, crítica;
  - Subject Key Identifier;
  - Name Constraints, crítica, com `permittedSubtrees` de `dNSName` para os nomes cobertos e `excludedSubtrees` de `iPAddress` `0.0.0.0/0` e `::/0`. A exclusão de IPs impede que a CA emita certificados para qualquer endereço IP. O .NET não tem builder para essa extensão, então ela é codificada com `System.Formats.Asn1` e coberta por teste de decodificação.
- **Sem domínios:** se não houver nenhuma rota, a CA não é gerada. Name Constraints sem nomes permitidos significaria uma CA sem restrição.
- **Folha:**
  - ECDSA P-256, validade de 397 dias;
  - SAN com o domínio, EKU `serverAuth`, KeyUsage `digitalSignature`;
  - Authority e Subject Key Identifier;
  - número de série aleatório de 16 bytes.
- **Armazenamento** em `%LOCALAPPDATA%\Severino\ca\`:
  - `ca.crt` (DER);
  - `ca.key` (PKCS#8 cifrado com DPAPI no escopo do usuário, com entropia própria do app);
  - `certs\<domínio>.pfx`, também cifrado com DPAPI.
- **Leitura da cobertura:** a cobertura atual é lida da extensão do próprio `ca.crt`, sem um registro paralelo que possa ficar inconsistente.
- **Confiança:** a CA vai para `X509Store(Root, CurrentUser)`. O aviso de segurança do Windows funciona como consentimento. Se o usuário recusar, a ativação é desfeita e os arquivos são apagados.

### Reemissão

1. A CA nova é gerada com a cobertura nova e instalada, e o Windows pede confirmação.
2. Os arquivos da CA nova substituem os da antiga, e o cache de folhas é esvaziado. As folhas são reemitidas sob demanda no próximo acesso.
3. A CA antiga é removida do repositório, e o Windows pede confirmação de novo. Se o usuário recusar, ela fica lá, órfã mas inofensiva, porque a chave dela foi apagada. A interface avisa.

Remover rotas não dispara reemissão. Configurações › HTTPS mostra os nomes cobertos e oferece "Reemitir" para enxugar a lista.

### Emissão por SNI (`Proxy`)

- **Seleção:** o `ServerCertificateSelector` do Kestrel recebe o SNI e responde só para rotas com HTTPS ligado e domínio coberto pela CA atual. Para o resto não há certificado, e o handshake falha.
- **Cache:** fica em memória, com o disco como segundo nível. A folha é renovada quando faltam 30 dias para vencer ou quando a CA muda. O relógio é injetável, via `TimeProvider`.
- **Carga:** o certificado é exportado para PKCS#12 e recarregado com `X509CertificateLoader.LoadPkcs12` antes de chegar ao Kestrel (ver "Validações iniciais").
- **Concorrência:** duas conexões simultâneas para um domínio novo não emitem duas folhas.

### Proxy HTTPS

- **Endereços:** Kestrel em `127.0.0.1` e `[::1]`, na porta `settings.httpsPort`, com HTTP/1.1 e HTTP/2 negociados por ALPN. Com o `CreateSlimBuilder`, isso exige `UseKestrelHttpsConfiguration()`.
- **Listener independente:** o HTTPS tem o seu próprio estado, com as mesmas regras de porta ocupada e de troca a quente da Fase 1. A barra inferior passa a mostrar "Proxy ativo :80 :443".
- **Redirecionamento:** um middleware no listener HTTP responde 307 quando a rota tem HTTPS e "Redirecionar" ligados. Preserva caminho e query, e inclui a porta quando ela não é a 443. O proxy nunca envia HSTS.
- **Encaminhamento:** o `X-Forwarded-Proto` chega como `https` ao destino. O YARP já faz isso a partir do esquema da requisição; um teste garante.
- **Proteção contra loop:** a regra passa a considerar também a porta HTTPS.

### Interface

- **Configurações › HTTPS:**
  - estado: desativado, ou ativo com validade e a lista de nomes cobertos;
  - "Ativar HTTPS", desabilitado sem rotas, com a dica "crie uma rota primeiro";
  - "Exportar CA (PEM)", que abre o diálogo de salvar e mostra a linha `NODE_EXTRA_CA_CERTS` pronta para copiar;
  - "Reemitir" e "Remover CA";
  - porta HTTPS.
- **Formulário da rota:**
  - "HTTPS" e "Redirecionar HTTP→HTTPS", ligados por padrão quando a CA está ativa;
  - com a CA inativa, os dois ficam desabilitados, com um link para ativar;
  - domínio fora da cobertura mostra um aviso amarelo, e ao salvar o app oferece reemitir;
  - em TLDs com HSTS preload, o aviso da Fase 1 dá lugar a "HTTPS obrigatório" e a caixa fica marcada e travada.
- **Lista:** um selo "https" nas rotas com HTTPS, e o link abre `https://` quando houver.
- **Barra inferior:** estado do HTTPS, como "HTTPS ok", "HTTPS desativado" ou "porta 443 em uso por …".

## Fora do escopo

- **Fase 3:**
  - detecção do Firefox e do `security.enterprise_roots.enabled` (a tela de HTTPS só traz uma dica em texto);
  - `Severino.exe --cleanup` para o desinstalador;
  - assistente de primeira execução;
  - mudar a cor do ícone da bandeja conforme o estado.
- **Fase 4:** certificados curinga (`*.callfred.sev`) e rotas por caminho.
- **Fora do plano:** Let's Encrypt e qualquer CA pública. HTTPS do proxy até o destino já existe desde a Fase 1, pelo "ignorar certificado inválido".

## Testes

**Unitários**

- **Cobertura:**
  - TLD inexistente vira o TLD inteiro, e TLD real vira o nome exato;
  - DNS sem resposta vira o nome exato;
  - redundâncias são removidas;
  - `IsCovered` em casos de borda: `sev` contra `a.b.sev`, e `empresa.com` contra `xempresa.com`, que não deve casar.
- **Codificação das Name Constraints:** ida e volta com `AsnReader`; extensão crítica; IPs excluídos.
- **CA:**
  - extensões e validade da raiz;
  - folha com SAN, EKU, KeyUsage, AKI e validade;
  - a folha encadeia na raiz com `X509Chain` em `CustomRootTrust`.
- **Name Constraints na cadeia do Windows:** uma folha para `banco.com.br` é rejeitada. Esse teste depende da validação inicial; se o Windows não aplicar a restrição na raiz, ele fica documentado como limitação, sem teste.
- **Armazenamento:** a chave em disco não começa com o cabeçalho de PKCS#8 em claro, a ida e volta pelo DPAPI funciona, e um arquivo corrompido leva a "CA inválida" em vez de travar o app.
- **Cache de folhas:**
  - reaproveita a folha;
  - renova quando faltam 30 dias, com relógio falso;
  - descarta tudo quando a CA muda;
  - emite uma única vez sob concorrência.

**Integração** (Kestrel com HTTPS em porta aleatória e a CA de teste como raiz confiável só no cliente)

- handshake com SNI de rota HTTPS e resposta 200 por HTTP/2;
- SNI sem rota, ou de rota sem HTTPS, falha no handshake;
- 307 com caminho e query preservados, e porta incluída fora da 443;
- nenhuma resposta com `Strict-Transport-Security`;
- `X-Forwarded-Proto: https` no destino;
- WebSocket por `wss://`;
- folha nova depois de trocar a CA, sem reiniciar o Kestrel.

**Checklist manual** (no fim da fase; roteiro passo a passo em [docs/testes/fase-2-https.md](../testes/fase-2-https.md))

- os critérios de pronto 1, 2, 4, 5, 6 (Chrome), 7 e 8;
- recusar o aviso do Windows na ativação desfaz tudo;
- "Remover CA" e depois "Ativar HTTPS" de novo funcionam.

## Ordem de implementação

1. Validações iniciais, num teste descartável.
2. `Core`: cobertura, consulta SOA e cache de TLD, com testes.
3. `Proxy/Certificates`: Name Constraints em ASN.1, CA, folhas, armazenamento com DPAPI e repositório do Windows, com testes.
4. `Proxy`: listener HTTPS, seletor por SNI, redirecionamento e estado por porta, com testes de integração.
5. `App`: Configurações › HTTPS, formulário, reemissão, exportação e barra inferior.
6. Checklist manual e ajustes.

## Pacotes novos

Nenhum. Tudo sai de `System.Security.Cryptography`, `System.Formats.Asn1` (que já vem no runtime) e do próprio Kestrel.

## Decisões a confirmar

Cada item já tem um padrão adotado neste spec. Basta confirmar ou trocar.

1. **A cobertura considera todas as rotas com HTTPS, ligadas ou não.** *Padrão: sim.* Religar uma rota não pede reemissão. O custo é que rotas desligadas continuam cobertas.
2. **Sem resposta do DNS, cobrir o nome exato.** *Padrão: sim.* É mais seguro, e o pior caso é uma reemissão a mais quando a rede voltar.
3. **Ativar HTTPS exige pelo menos uma rota.** *Padrão: sim.* Uma CA sem nomes permitidos não teria restrição nenhuma. A alternativa seria já incluir `sev` por padrão.
4. **Rota sem HTTPS recusa o handshake em vez de servir certificado.** *Padrão: recusar.* O navegador mostra erro de conexão em `https://` para essa rota, o que deixa claro que o HTTPS está desligado nela.
5. **HTTPS e redirecionamento ligados por padrão nas rotas novas quando a CA está ativa.** *Padrão: sim*, como no esboço do formulário no plano.
