# Changelog

[English](../../CHANGELOG.md) | **Português**

Todas as mudanças relevantes do Severino ficam neste arquivo.

O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/), e o projeto adota o [Versionamento Semântico](https://semver.org/lang/pt-BR/spec/v2.0.0.html).

## [Unreleased]

### Adicionado

- Detalhes da requisição: clicar numa linha de Requisições abre os cabeçalhos, os tamanhos, para onde ela foi e, com **Guardar corpos** ligado, os primeiros 64 KB de cada corpo de texto. **Copiar como curl** (PowerShell ou bash), **Repetir** e **Exportar HAR**.
- Avisos de saúde: rotas e serviços com **Avisar quando parar de responder** ganham um aviso na bandeja depois de 15 segundos sem resposta, e outro quando voltam. Clicar abre a aba. Configurações › Geral desliga todos.
- Regras por rota, no **Avançado** da rota: liberar CORS (o Severino responde o preflight), pôr ou tirar cabeçalhos na ida e na volta, esperar antes de repassar e falhar de propósito com 429, 500, 502, 503 ou 504 em parte das requisições. Cada regra ligada aparece como selo no card da rota.
- Pasta como destino de uma rota: o Severino serve os arquivos dela, com index.html, sem cache e, para apps de página única, páginas desconhecidas indo para o index.html. Rotas por caminho, HTTPS e as regras por rota também valem para pasta.
- Ambientes: rotas, serviços e entradas DNS podem ser de um, como `producao` ou `staging`, e o mesmo nome pode existir em vários. Só o ambiente ativo vale (com os itens sem ambiente); a barra de status e a bandeja trocam, e hosts, proxy, serviços e WSL acompanham na hora. `Get-SeverinoEnvironment` e `Set-SeverinoEnvironment` no PowerShell.

### Alterado

- Clicar no aviso de versão nova na bandeja abre a janela.

### Corrigido

- Os editores (rota, serviço, entrada DNS) e o assistente de primeiro uso às vezes abriam em branco, só com a moldura e o cursor de texto.

## [0.5.1] - 2026-10-11

### Corrigido

- "Sair" na bandeja deixava o menu congelado na tela e o processo aberto.
- A aba DNS tem menos texto: dicas de seção mais curtas, um estado vazio compacto, o "ler de novo" junto ao título da seção e sem o selo "fora do Severino" repetido em cada linha.
- A barra de rolagem das listas não cobre mais os botões dos cards.

## [0.5.0] - 2026-10-10

Primeira versão publicada.

### Adicionado

- Rotas: um domínio, como `meuapp.sev`, leva a um app local por um proxy reverso que escuta só em `127.0.0.1` e `::1`, com o nome gravado num bloco próprio do arquivo hosts.
- Rotas por caminho: `meuapp.sev/api` vai para outro destino que o resto do domínio, com a opção de tirar o caminho antes de repassar.
- Rotas curinga: `*.meuapp.sev` atende qualquer nome abaixo, respondido por um DNS do serviço auxiliar em `127.53.0.1` e por regras NRPT do Windows.
- Grupos de rotas, com uma chave que liga ou desliga o grupo inteiro e um menu para renomear ou desfazer o grupo.
- HTTPS por uma autoridade certificadora local ECDSA P-256, com Name Constraints restritos aos domínios das rotas, chave protegida por DPAPI e instalada só na conta do usuário.
- Redirecionamento de HTTP para HTTPS com 307, nunca 301 nem HSTS.
- Serviços: nomes que levam direto a uma porta, em qualquer protocolo (Postgres, Redis, gRPC), cada um num endereço de loopback próprio.
- Importação de serviços do Kubernetes (NodePort, ClusterIP por `kubectl port-forward` gerenciado, hosts de Ingress como rotas) e de containers do Docker, no Windows ou numa distro do WSL.
- Acompanhamento dos serviços importados: eventos do Docker e consulta ao Kubernetes a cada 30 segundos corrigem portas sozinhos.
- Apps no WSL chamam os serviços pelo nome, nas distros escolhidas.
- Aba DNS: entradas que levam um nome direto a um IP e valem com o app fechado. IPs privados entram na hora; IP público e curinga pedem confirmação de administrador do Windows.
- As linhas do hosts que não são do Severino aparecem marcadas; editar ou remover uma delas pede confirmação e deixa um comentário no hosts.
- Nomes do DNS como destino de rotas e serviços, com o IP à vista e links entre as abas.
- O formulário de rota sugere criar um serviço quando a porta é de um banco ou fila, e uma entrada DNS quando o destino é um IP da rede.
- Aba Requisições, com o log em memória do que passou pelo proxy e pelos serviços.
- Ícone na bandeja com a lista de rotas e uma pausa que devolve os domínios à internet.
- Módulo PowerShell que fala com o app aberto: `New-SeverinoRoute`, `Set-SeverinoDns`, `Disable-SeverinoRoute -Group` e outros.
- Backup de rotas, serviços e entradas DNS num arquivo JSON.
- Assistente de primeiro uso, que confere a porta 80, o serviço auxiliar e o proxy do sistema.
- Aviso de versão nova: o app consulta a última release no GitHub ao abrir e uma vez por dia, e avisa no topo da janela e na bandeja. **Atualizar agora** baixa o instalador, confere o SHA-256 publicado pelo GitHub, instala em silêncio e abre o app de novo, mantendo rotas, serviços e configurações. A consulta pode ser desligada em Configurações › Sobre.
- Inglês e português: o app, o instalador e as páginas de erro do proxy seguem o idioma do Windows, e Configurações › Geral deixa escolher.
- "Limpar tudo" e o desinstalador tiram o que o Severino colocou no Windows.
- Instalador para Windows 10 e 11, 64 bits.
- Site em <https://lucassm02.github.io/severino/>, em inglês e em português, com vídeo de apresentação e imagens para redes sociais.

[Unreleased]: https://github.com/lucassm02/severino/compare/v0.5.1...HEAD
[0.5.1]: https://github.com/lucassm02/severino/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/lucassm02/severino/releases/tag/v0.5.0
