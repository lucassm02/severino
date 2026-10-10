# Revisão da UI depois das Fases 4 e 6

Status: aprovada em 2026-10-10, com todas as recomendações (ver "Decisões" no fim), e implementada no mesmo dia. Falta o teste manual, na seção 13 do [roteiro da Fase 6](../testes/fase-6-dns-e-extras.md).

Base: telas renderizadas do app real com dados de exemplo (rotas com grupo, caminho e curinga; serviços do Kubernetes e do Docker; entradas DNS e linhas de fora; janela de importação com Ingress; editores; Configurações).

## Diagnóstico

O Severino hoje dá nome a coisas de três jeitos diferentes:

| Tipo | O que faz | Quando usar | Onde está na UI |
| --- | --- | --- | --- |
| Rota | nome → proxy → app web, com HTTPS, caminho, curinga e grupo | abrir no navegador | aba Rotas, em cima |
| Serviço | nome → endereço próprio em loopback → destino, em qualquer protocolo | banco, fila, gRPC, outro programa | aba Rotas, embaixo |
| Entrada DNS | nome → IP, sem proxy, vale com o app fechado | máquina da rede, VPN, gateway | aba DNS |

As peças estão certas, e cada uma funciona bem sozinha. O problema é como estão apresentadas.

1. **A aba Rotas guarda dois conceitos.** Serviços aparecem depois das rotas, com um botão "Novo serviço" discreto e uma frase técnica ("Cada nome vai direto ao destino, em qualquer protocolo, com o nome intacto"). Quem não leu a documentação não sabe por que uma coisa é rota e outra é serviço, nem qual criar. "Importar serviços" fica no cabeçalho das rotas e agora também cria rotas, a partir do Ingress.
2. **As ligações entre os três existem, mas não aparecem.** Uma rota ou um serviço pode apontar para `gateway.k8s` da aba DNS, mas isso só se vê no texto do destino. "Destino de 3 serviços" no DNS não é clicável. Transformar uma rota em entrada DNS está escondido no menu "…". O editor de serviço lista os nomes do DNS numa frase corrida.
3. **Nada ajuda a escolher o tipo certo.** Uma rota para `localhost:5432` não funciona, porque o Postgres não fala HTTP, e o editor não avisa. Uma rota para `10.0.0.8:80` provavelmente devia ser uma entrada DNS, e o editor também não sugere.
4. **Detalhes internos aparecem na lista.** Toda linha de serviço mostra `127.77.0.2`, o endereço de loopback que o Severino escolheu. Para quem usa, o que importa é `80 → gateway.k8s:30080`.
5. **O que é estrutural fica escondido no "Avançado".** No editor de rota, Caminho e Grupo dividem a seção com "Preservar o Host" e "Ignorar certificado inválido". A seção HTTPS repete o título no checkbox ("HTTPS / HTTPS"). Não há pista de que `*.nome` cria um curinga.
6. **A aba DNS grita nas linhas de fora.** Cada linha leva borda laranja e a frase "Fora do Severino: não foi criada por ele". Com 20 linhas no hosts, isso vira uma parede laranja. Os avisos fortes que você pediu valem para a hora de mexer, e o editor e a confirmação já fazem isso. Na lista, basta deixar a linha marcada.
7. **Configurações cresceu por acréscimo.**
   - As portas HTTP e HTTPS estão separadas pelo cartão de certificados.
   - "Acompanhar serviços importados" está sob o título WSL, mas vale também para o Kubernetes e o Docker no Windows.
   - "Rotas: Exportar/Importar" só leva as rotas; serviços manuais e entradas DNS não viajam.
   - "Limpar tudo" ainda descreve "o bloco do hosts", no singular, sem as entradas DNS e as regras de curinga, que hoje também são removidas.
   - O módulo PowerShell não aparece em lugar nenhum da UI.
8. **Vocabulário de estado varia.** Rotas e serviços usam "Respondendo/Desligada", e o DNS usa "Valendo". "Ligada" aparece em outro sentido no WSL.

O que funciona e deve ficar como está:

- a navegação por abas;
- o desenho da linha (estado, chave liga/desliga, menu "…");
- a barra de status com correção em um clique;
- a aba Requisições;
- o editor de DNS, com antes/depois para as linhas de fora;
- os grupos de rotas;
- o desfazer depois de remover.

## Proposta

Dois níveis. O primeiro é só ajuste e resolve os itens 4 a 8. O segundo inclui o primeiro e resolve também os itens 1 a 3, que causam a confusão que você descreveu.

### Nível 1: ajustes (sem mudar a estrutura)

**Rotas e editor de rota**

- O endereço vira um campo só: `callfred.sev/api` no campo do domínio já cria a rota com caminho. "Tirar o caminho antes de repassar" aparece logo abaixo, só quando houver caminho. O campo Caminho separado deixa de existir.
- Uma dica sob o campo: "Use *.callfred.sev para atender qualquer nome abaixo dele."
- Grupo sai do Avançado e vira um campo opcional visível.
- HTTPS vira uma linha só: "Usar HTTPS" e, abaixo, "Redirecionar HTTP para HTTPS".
- O Avançado fica com o que é raro: preservar o Host, ignorar certificado inválido e observações.
- O cabeçalho do grupo ganha um menu: Nova rota neste grupo, Renomear grupo, Desfazer grupo.

**Serviços**

- A linha mostra só `80 → gateway.k8s:30080` (ou `5672 → port-forward`). O `127.77.0.2` vai para a dica de ferramenta e para o editor.
- No editor, os nomes do DNS viram sugestões no próprio campo, como já acontece no editor de rota, em vez da frase.
- Na janela de importação:
  - "Nó das NodePorts" passa a "Endereço do cluster", com dica explicando;
  - o Ingress ganha um cabeçalho de seção ("Ingress: viram rotas web");
  - o rodapé conta os tipos separados ("3 serviços e 1 rota marcados");
  - a linha de port-forward sai da fonte monoespaçada.

**DNS**

- Linhas de fora:
  - o aviso aparece uma vez, no cabeçalho da seção;
  - cada linha leva um selo pequeno "fora do Severino" e a barra lateral;
  - "já editada pelo Severino" vira um segundo selo.
- O editor e a confirmação de antes/depois continuam como estão, com os avisos fortes.
- O estado passa a "Ativa", alinhado ao resto. Os estados do app ficam: Respondendo, Fora do ar, Ativa, Desligada e Aguardando aprovação.

**Configurações**

- Rede: as portas HTTP e HTTPS juntas.
- HTTPS: só certificados.
- Serviços: "Acompanhar serviços" e as distros do WSL.
- Terminal (novo): se o módulo PowerShell está instalado e três exemplos para copiar.
- Geral: tema e inicialização juntos.
- Manutenção:
  - "Backup" exporta e importa rotas, serviços manuais, entradas DNS e grupos, em vez de só as rotas (o arquivo atual continua sendo lido);
  - "Limpar tudo" lista o que de fato tira.

### Nível 2: remodelação leve (recomendado)

Tudo do nível 1, mais:

**Serviços em aba própria.** As abas passam a ser `Rotas | Serviços | DNS | Requisições | Configurações`. Cada aba tem um conceito, um botão principal e uma frase que diz para que serve:

- **Rotas:** "Para abrir no navegador. O nome passa pelo proxy, com HTTPS, caminhos e curingas." Botão: Nova rota.
- **Serviços:** "Para bancos, filas e outros programas. O nome leva direto à porta, em qualquer protocolo." Botões: Importar do Kubernetes ou Docker, Novo serviço.
- **DNS:** "Um nome direto para um IP, sem proxy. Vale com o app fechado." Botão: Nova entrada.

Se um import de Ingress criar rotas, o aviso no fim diz "2 rotas web criadas na aba Rotas", com link.

```
 Rotas   Serviços   DNS   Requisições   Configurações
 ───────────────────────────────────────────────────────────────
 Serviços                                 [Buscar]  [Importar]  [+ Novo]
 Para bancos, filas e outros programas. O nome leva direto à porta.

 Kubernetes · dev-cluster · WSL Ubuntu            ↻ Atualizar   …
 ┌──────────────────────────────────────────────────────────────┐
 │ ● pedidos  +3 nomes · loja                 Respondendo  ◉  … │
 │   80 → gateway.k8s:30080                                     │
 ├──────────────────────────────────────────────────────────────┤
 │ ● fila-pedidos · loja                      Respondendo  ◉  … │
 │   5672 → port-forward                                        │
 └──────────────────────────────────────────────────────────────┘
```

**Ligações visíveis.**

- Um destino por nome mostra o IP ao lado, discreto (`gateway.k8s · 192.168.203.100`), e clicar leva à entrada na aba DNS.
- No DNS, "Destino de 3 serviços" vira link, que abre a aba Serviços filtrada por esse destino.
- A busca de cada aba encontra pelo nome do destino.

**O editor ajuda a escolher o tipo.**

- **Porta conhecida de banco ou fila no destino de uma rota** (5432, 3306, 1433, 6379, 27017, 5672, 9092): o editor avisa "Isso parece um banco, que não fala HTTP. Como serviço, o nome leva direto à porta." e oferece o botão "Criar como serviço", que leva o nome e o destino junto.
- **IP de rede como destino de uma rota sem HTTPS nem caminho:** o editor oferece "Só quer o nome apontando para esse IP? Criar entrada DNS". Isso substitui o item escondido no menu "…" (que fica, para rotas já existentes).
- **Telas vazias:** cada aba vazia mostra o mesmo quadro de três linhas, "Rota, Serviço ou DNS: qual usar", com o caso típico de cada uma.

### O que fica de fora

- Sidebar no lugar das abas. Cinco abas cabem na largura mínima de 640 px; a sidebar só se paga com mais seções.
- Uma lista única de "nomes" misturando os três tipos. Os três têm colunas, ações e regras diferentes, e misturar deixaria cada linha mais confusa.
- Bandeja: listar grupos como chaves seria bom, mas é independente e pode vir depois.

## Tamanho

| Item | Tamanho |
| --- | --- |
| Nível 1, Rotas e editor (endereço com caminho, grupo visível, HTTPS, menu do grupo) | médio |
| Nível 1, Serviços (linha, sugestões, importação) | pequeno |
| Nível 1, DNS (selos, estado) | pequeno |
| Nível 1, Configurações (reagrupar, Terminal, Backup completo, texto do Limpar tudo) | médio; o Backup mexe no formato do arquivo |
| Nível 2, aba Serviços (separar o view model de serviços do de rotas, mover import) | médio |
| Nível 2, ligações (IP ao lado, links entre abas, busca por destino) | pequeno |
| Nível 2, editor que sugere o tipo e telas vazias | médio |

Cada bloco sai num commit próprio, com testes nos view models e as telas renderizadas para conferir, no mesmo esquema das fases.

## Decisões

1. Nível 2, que inclui o nível 1.
2. O caminho vai no campo do endereço (`callfred.sev/api`).
3. Linhas de fora do hosts: selo discreto na lista; os avisos fortes ficam no editor e na confirmação.
4. O Backup completo entra agora e substitui o export só de rotas. O arquivo antigo, só com rotas, continua sendo lido.

