# Changelog

Todas as mudanças relevantes do Severino ficam neste arquivo.

O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/), e o projeto adota o [Versionamento Semântico](https://semver.org/lang/pt-BR/spec/v2.0.0.html).

## [Unreleased]

Ainda não há versão publicada. Tudo abaixo entra na primeira.

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
- "Limpar tudo" e o desinstalador tiram o que o Severino colocou no Windows.
- Instalador para Windows 10 e 11, 64 bits.
- Site em <https://lucassm02.github.io/severino/>, com imagens para redes sociais.

[Unreleased]: https://github.com/lucassm02/severino/commits/main
