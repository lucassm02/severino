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
