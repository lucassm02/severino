# Spec: Fase 11, Serviços com HTTP

Status: pedida e implementada em 2026-10-13; falta o roteiro manual do [guia de testes](../testes/fase-11-servicos-http.md). As escolhas em "Decisões" são os padrões adotados.

## Objetivo

Os serviços vindos do Docker e do Kubernetes são encaminhamentos TCP: o que o app manda chega ao destino byte a byte, em qualquer protocolo. Isso não deixa usar com eles o que as rotas têm na camada HTTP. Esta fase traz duas coisas:

1. **Portas HTTP.** Uma porta marcada como HTTP passa pelo proxy em vez do túnel TCP e ganha o que as rotas têm: HTTPS pela CA local, as regras (CORS, cabeçalhos, espera, falha simulada) e as requisições com detalhes na aba Requisições.
2. **Abrir no navegador.** Clicar no nome do serviço abre o endereço dele, como nas rotas.

Nada muda nas decisões fixas: o proxy continua só em loopback e o Helper continua gravando só loopback.

## Como funciona

O serviço continua com o seu endereço de loopback (`127.77.x.y`) e escutando nas suas portas. Uma porta HTTP, em vez de ir direto ao destino, encaminha a conexão para o próprio proxy em `127.0.0.1`. O que o app mandou chega ao proxy inteiro, inclusive o `Host` (`catalogoapi:8080`) e o SNI. O proxy escolhe a rota interna do serviço pelo nome e pela porta e repassa ao destino, com as regras no caminho.

- **Uma rota interna por porta HTTP**, casando `nome:porta` para cada nome do serviço. Ela não aparece na aba Rotas.
- **O `Host` original vai para o destino**, como no túnel TCP. O destino recebe o que recebia antes.
- **HTTPS.** Com HTTPS ligado, o serviço passa a responder também na porta 443 do seu endereço: `https://catalogoapi` vai para a porta HTTP principal, que é a 80 quando ela é HTTP e senão a primeira HTTP. Isso só não vale quando o serviço já tem uma porta 443 própria. Nas outras portas HTTP, o primeiro byte da conexão diz se é TLS: `https://catalogoapi:8080` e `http://catalogoapi:8080` funcionam os dois.
- **O certificado** sai da CA local para os nomes do serviço, que entram na cobertura da CA como os domínios das rotas. Um nome fora da cobertura pede a reemissão, como numa rota.
- **Requisições.** Uma porta HTTP aparece na aba Requisições como HTTP, com detalhes, e não como linha TCP. Ela só aparece como TCP quando o proxy não está escutando.

## Critérios de pronto

1. **Marcar.** Cada linha de porta no editor do serviço ganha a caixa **HTTP**. Ela vem desligada em tudo o que já existe e no que é importado: marcar é escolha da pessoa.
2. **Seção HTTP.** Com alguma porta HTTP, o editor mostra **Usar HTTPS** (com a CA ativa e o aviso de cobertura das rotas) e a seção **Regras**, igual à das rotas. Sem porta HTTP, ligar HTTPS ou ter regras é erro de validação, com o motivo.
3. **Atualizar do Docker ou do Kubernetes** mantém a marca HTTP das portas que continuam existindo, pelo número da porta.
4. **Card.** O card do serviço mostra o selo HTTPS e os selos das regras, como o card da rota.
5. **Abrir.** O nome do serviço é um link:
   - uma porta para abrir: abre direto;
   - várias: um menu lista os endereços;
   - nenhuma: o nome não é link.
   Entram as portas HTTP (`https://` quando o HTTPS do serviço vale) e as outras que não são de banco nem de fila (`http://nome:porta`; a 443 sem marca abre `https://nome`). O menu ⋯ do card ganha **Abrir no navegador** com os mesmos endereços.
6. **Requisições.** A linha mostra o nome do serviço em "Rota" nos detalhes.
7. **Arquivos.** Backup, arquivo de projeto e `Export-SeverinoProject` levam a marca HTTP, o HTTPS e as regras. Versões anteriores ignoram os campos novos.

## Decisões

- **Sem redirecionar de HTTP para HTTPS nos serviços.** Quem chama um serviço costuma ser outro app, não o navegador; um 307 inesperado quebra clientes. Quem quiser só HTTPS chama `https://`.
- **Sem marcação automática na importação.** Passar pelo proxy muda o comportamento: o proxy fala HTTP/1.1 e HTTP/2 com TLS, e um gRPC sem TLS (h2c) numa porta marcada deixaria de funcionar. A marca fica com a pessoa.
- **WSL.** Na distro, o iptables leva o app direto ao destino, sem passar pelo Windows. Ali as portas HTTP continuam como hoje: sem HTTPS do Severino e sem regras. A seção HTTP do editor diz isso.
- **Saúde.** O aviso de saúde continua olhando o destino, não o proxy.

---

## Parte 2. Acesso às rotas

Pedida em 2026-10-13. Uma rota pode pedir credencial antes de repassar. Serve para simular um ambiente protegido, como um staging atrás de login ou uma API atrás de gateway, e para não deixar uma rota aberta a qualquer página do navegador. Não substitui a autenticação do próprio app: o proxy só escuta na máquina, e quem chega já está nela.

### Modos

A seção **Acesso** do editor da rota escolhe um:

- **Aberta.** Como hoje, o padrão.
- **Tela de senha do Severino.** Quem abre a rota no navegador vê uma página do Severino com usuário e senha. Acertando, ganha um cookie de sessão da rota e volta para onde ia. O cookie vale 12 horas ou até o navegador fechar, e cai na hora em que a senha muda. Requisições que não são de página (`fetch`, `curl`) sem o cookie recebem 401, sem redirecionar.
- **Basic Auth.** O proxy responde 401 com `WWW-Authenticate: Basic`, e o navegador pergunta usuário e senha.
- **Cabeçalho obrigatório.** Só passa quem manda o cabeçalho escolhido com o valor certo, como `X-Api-Key`. O cabeçalho segue para o destino.

### Critérios de pronto

1. **Senha guardada como hash.** A senha vira PBKDF2-SHA256 com sal, e a chave do cabeçalho vira SHA-256, no `config.json`. Editar sem digitar senha nova mantém a atual.
2. **Assinatura do cookie.** É um HMAC com uma chave da máquina, guardada com DPAPI ao lado da configuração. Reiniciar o app mantém as sessões.
3. **Endereços da tela.** A tela fica em `/.severino/login` sob o caminho da rota, e `/.severino/logout` encerra a sessão. O retorno só aceita caminho do próprio domínio, sem redirecionamento aberto.
4. **CORS.** Com CORS ligado na rota, o preflight passa sem credencial, porque o navegador não manda credencial nele.
5. **Pasta como destino** também é protegida.
6. **Card.** A rota protegida mostra um selo com o modo: `senha`, `basic` ou `chave`.
7. **Requisições.** As respostas do próprio Severino (tela, 401) aparecem com o selo Severino.
8. **Arquivos.** Backup e arquivo de projeto levam o modo, o usuário e os hashes, nunca a senha.

### Decisões

- **Um modo por rota.** Tela de senha e Basic Auth fazem a mesma coisa de jeitos diferentes; o cabeçalho é para clientes que não são navegador.
- **Sem bloqueio por IP.** O proxy só aceita conexões da própria máquina, então o endereço de quem chama é sempre 127.0.0.1 ou ::1.
- **Sem limite de tentativas.** Cada senha errada espera meio segundo antes de responder. Basta para uma rota local.
