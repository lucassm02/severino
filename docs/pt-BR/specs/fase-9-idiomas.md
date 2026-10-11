# Spec: Fase 9, Inglês e português

Status: aprovada em 2026-10-10, com os padrões das "Decisões a confirmar", e implementada no mesmo dia. Falta o [roteiro manual](../testes/fase-9-idiomas.md). Na implementação, as mensagens das outras camadas mudaram de abordagem: ver "Mensagens que vêm de outras camadas".

## Objetivo

O Severino passa a falar inglês, sem deixar de falar português:

- O repositório tem o inglês como idioma principal. Quem chega pelo GitHub lê o README e o CHANGELOG em inglês na raiz, com um link para a versão em português.
- Os documentos em português ficam numa pasta própria, `docs/pt-BR/`.
- O app, o instalador e o que o Severino mostra no navegador existem nos dois idiomas. Por padrão o app segue o idioma do Windows, e dá para trocar nas Configurações.

## Critérios de pronto

1. **Raiz em inglês.** `README.md` e `CHANGELOG.md` da raiz estão em inglês. Cada um abre com `English | Português`, que leva ao par em `docs/pt-BR/`, e o par em português leva de volta.
2. **Português preservado.** `docs/pt-BR/README.md` e `docs/pt-BR/CHANGELOG.md` têm o mesmo conteúdo da raiz, em português. Nenhum link interno quebra depois da mudança (conferido por script).
3. **App nos dois idiomas.** Com o Windows em inglês, o app abre em inglês. Com o Windows em português, abre em português. Em **Configurações › Aparência › Idioma** dá para escolher Automático, English ou Português (Brasil).
4. **Nada esquecido.** Em inglês, nenhum texto em português aparece em nenhuma tela: abas, formulários, assistente de primeira execução, bandeja, notificações, avisos, mensagens de erro do serviço auxiliar e páginas de erro do proxy. Um teste compara as chaves dos dois idiomas, e outro procura texto em português fora dos arquivos de recurso.
5. **Instalador nos dois idiomas.** O instalador escolhe o idioma pelo Windows, inclusive nas perguntas da desinstalação.
6. **Release em inglês.** As notas da release saem do `CHANGELOG.md` em inglês, com um link para o changelog em português.

## Repositório e documentos

| Hoje | Depois |
|---|---|
| `README.md` (pt-BR) | `README.md` em inglês, `docs/pt-BR/README.md` em português |
| `CHANGELOG.md` (pt-BR) | `CHANGELOG.md` em inglês, `docs/pt-BR/CHANGELOG.md` em português |
| `docs/planejamento.md`, `docs/specs/`, `docs/testes/` | `docs/pt-BR/planejamento.md`, `docs/pt-BR/specs/`, `docs/pt-BR/testes/` (decisão 2) |
| `CLAUDE.md` (pt-BR) | Em inglês (decisão 3) |
| `LICENSE` | Já está em inglês |

- O CHANGELOG em inglês vira o principal. A seção `[Unreleased]` é preenchida nos dois arquivos no mesmo commit. O workflow **Release** continua lendo a seção da versão, agora no arquivo da raiz.
- As capturas do README e do site existem nos dois idiomas, geradas pelo harness de renderização do app, com os mesmos dados de exemplo (`meuapp.sev`, `pedidos.loja`, `banco.interno`).
- A descrição do repositório no GitHub e o social preview passam para o inglês. O social preview em português continua disponível para quem divulgar em português.

## O app

**Recursos.** Os textos saem do código e do XAML para arquivos `.resx` no `Severino.App`: `Strings.resx` com o inglês (idioma neutro, usado quando falta uma tradução) e `Strings.pt-BR.resx` com o português. O `Directory.Build.props` já limita os recursos satélites a `pt-BR;en`. A medição de hoje acha perto de 570 textos em português, a maior parte no App (cerca de 440, entre XAML e C#), mais uns 85 no Core.

**No XAML.** Uma extensão de marcação curta, como `Text="{l:T Routes.Empty}"`, lê o recurso. Textos com valores (`{0} rotas ativas`) usam formato com placeholders nomeados nos ViewModels, nunca concatenação, porque a ordem das palavras muda entre os idiomas.

**Escolha do idioma.**

- Campo novo `Language` no `SeverinoConfig`, com o valor `Auto` por padrão, como o `Theme`.
- `Auto`: o app usa português se o idioma de interface do Windows for `pt-*`, e inglês em qualquer outro caso.
- O idioma é aplicado na partida, antes da primeira janela (`CultureInfo.DefaultThreadCurrentUICulture`). Trocar nas Configurações pede para reiniciar o app (decisão 4).

**Mensagens que vêm de outras camadas.**

- **Contracts, Core e Proxy** têm cada um o seu par de `.resx`. Eles rodam no processo do App, que define o idioma na partida, então as mensagens já saem no idioma escolhido. O Helper continua sem depender de nada além do Contracts. *Mudança na implementação:* o rascunho previa códigos de erro traduzidos no App, mas isso mexia no protocolo e em todas as validações para o mesmo resultado.
- **Helper**: roda como serviço do Windows, fora da sessão do usuário, e não sabe o idioma dele. As mensagens dele, e as do Contracts quando rodam nele, ficam em inglês. O App valida tudo antes de mandar, então essas mensagens só aparecem num erro técnico que o App não previu.
- **Proxy**: as páginas de erro mostradas no navegador (app fora do ar, rota desligada, certificado) usam o idioma escolhido no app.
- **Módulo PowerShell**: as mensagens que vêm do app chegam no idioma do app. As poucas mensagens do próprio módulo e a ajuda dos cmdlets ficam em inglês.

**O que não muda de idioma.** Os nomes técnicos (rota, serviço, NodePort, port-forward, hosts), os logs e as marcações do bloco no arquivo hosts (`# >>> Severino managed block`) continuam iguais nos dois idiomas. Os logs passam para o inglês (decisão 5), porque servem para abrir issue.

**Datas e números.** Seguem o formato regional do Windows, como no resto do sistema; só os textos trocam de idioma. *Mudança na implementação:* o rascunho previa a cultura do app também para datas, o que trocaria o formato de quem usa o Windows em português com o app em inglês.

## Instalador

- `[Languages]` passa a ter `english` (padrão) e `brazilianportuguese`. O Inno escolhe pelo idioma do Windows, sem perguntar (`ShowLanguageDialog=auto`).
- Os textos próprios do `severino.iss` (falha ao registrar o serviço, perguntas da desinstalação, "Abrir o Severino") vão para `[CustomMessages]`, com as duas versões.
- O idioma escolhido no instalador não é gravado no app: os dois seguem o Windows, e quem quiser outro troca nas Configurações.

## Testes

- **Paridade:** cada chave do `Strings.resx` existe no `Strings.pt-BR.resx` e vice-versa, com os mesmos placeholders.
- **Varredura:** nenhum literal com cara de texto em português (acentos ou palavras como "não" e "para") fora dos `.resx`, dos testes e de uma lista de exceções.
- **Cobertura:** os quatro pares de `.resx` (App, Core, Contracts, Proxy) entram na paridade e na varredura.
- **Testes atuais:** os que conferem mensagens passam a fixar a cultura, para não dependerem do Windows de quem roda.
- **Visual:** o harness de renderização tira as telas principais nos dois idiomas, para conferir corte de texto e quebra de linha.
- **Roteiro manual** em `docs/pt-BR/testes/fase-9-idiomas.md`: instalar com o Windows em inglês, trocar o idioma, ver as páginas de erro e desinstalar.

## Decisões a confirmar

1. **Pasta `docs/pt-BR/`** para os documentos em português. *Padrão: sim.* O nome segue o código do idioma, o mesmo usado nos `.resx` e no instalador. Alternativa: `i18n/pt-BR/`.
2. **Specs, planejamento e roteiros de teste em português**, movidos para `docs/pt-BR/`, sem tradução. *Padrão: sim.* São cerca de 200 KB de documento interno de design. O README em inglês resume a arquitetura, e as specs novas podem ser escritas em qualquer um dos dois idiomas.
3. **`CLAUDE.md` em inglês.** *Padrão: sim*, junto com a raiz. A convenção do CHANGELOG passa a ser: entrada nos dois arquivos.
4. **Trocar o idioma pede reinício.** *Padrão: sim.* Trocar ao vivo exige que todo texto seja ligado por binding que avise a mudança, o que custa mais e é usado uma vez. Alternativa: troca ao vivo.
5. **Logs em inglês.** *Padrão: sim.*
6. **Site em inglês na raiz e português em `/pt-BR/`.** *Padrão: sim, nesta fase*, com `hreflang` entre as duas páginas e um seletor de idioma no topo. Inclui uma versão em inglês do vídeo de apresentação, renderizada pelo mesmo `intro.html` com os textos trocados. Alternativa: o site fica só em português e vira uma fase à parte.

## Fora do escopo

- Outros idiomas além de inglês e português. A estrutura de recursos permite incluir depois, sem mudar código.
- Tradução das specs e roteiros já escritos (decisão 2).
- Tradução por serviço externo ou plataforma de tradução colaborativa.

## Etapas

1. Repositório: mover os documentos para `docs/pt-BR/`, escrever o README e o CHANGELOG em inglês, corrigir links, ajustar o workflow Release. Um script confere os links relativos.
2. Infraestrutura no app: `Strings.resx`, extensão de marcação, campo `Language`, escolha na partida e seletor nas Configurações.
3. Recursos no Contracts, no Core e no Proxy; o Helper fixa o inglês.
4. Migração dos textos, tela por tela: Rotas, Serviços, DNS, Requisições, Configurações, editores, assistente, bandeja e páginas de erro do proxy.
5. Instalador com os dois idiomas.
6. Testes de paridade e varredura, e capturas nos dois idiomas.
7. Site e vídeo em inglês, se a decisão 6 ficar como está.
8. Roteiro de teste manual.
